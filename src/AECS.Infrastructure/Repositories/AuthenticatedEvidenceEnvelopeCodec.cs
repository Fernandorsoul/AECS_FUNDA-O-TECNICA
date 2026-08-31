using System.Security.Cryptography;
using AECS.Domain.Exceptions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Cryptography;

namespace AECS.Infrastructure.Repositories;

internal sealed class AuthenticatedEvidenceEnvelopeCodec
{
    private const string ExecutionPayloadKind = "execution-evidence";
    private const string PromotionPayloadKind = "candidate-promotion";
    private const string ChainPayloadKind = "evidence-chain-head";

    private readonly IEvidenceSignatureService _signatures;

    public AuthenticatedEvidenceEnvelopeCodec(IEvidenceSignatureService signatures)
    {
        ArgumentNullException.ThrowIfNull(signatures);
        _signatures = signatures;
    }

    public SignedExecutionEvidenceEnvelope Create(ExecutionEvidence evidence)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateEvidenceStructure(evidence);
        if (evidence.Id == Guid.Empty)
            throw new EvidenceIntegrityException("Execution evidence must have a non-empty ID.");
        if (evidence.Promotions.Count != 0)
        {
            throw new EvidenceIntegrityException(
                "Initial execution evidence cannot contain mutable promotion records.");
        }

        var signedAt = DateTime.UtcNow;
        var seal = CreateSeal(CreateExecutionPayload(
            evidence,
            _signatures.Algorithm,
            _signatures.KeyId,
            signedAt), signedAt);
        var envelope = new SignedExecutionEvidenceEnvelope
        {
            SchemaVersion = EvidenceEnvelopeFormat.CurrentSchemaVersion,
            Evidence = evidence,
            Seal = seal
        };
        envelope.ChainSeal = CreateChainSeal(envelope);
        return envelope;
    }

    public void AppendPromotion(
        SignedExecutionEvidenceEnvelope envelope,
        CandidatePromotionEvidence promotion)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(promotion);
        Validate(envelope, envelope.Evidence.Id);
        ValidatePromotion(envelope, promotion);

        var sequence = envelope.PromotionEvents.Count + 1;
        var previousSignature = envelope.PromotionEvents.Count == 0
            ? envelope.Seal.Signature
            : envelope.PromotionEvents[^1].Seal.Signature;
        envelope.PromotionEvents.Add(CreatePromotionEvent(
            envelope.Evidence.Id,
            sequence,
            previousSignature,
            promotion));
        envelope.ChainSeal = CreateChainSeal(envelope);
    }

    public void Validate(
        SignedExecutionEvidenceEnvelope envelope,
        Guid expectedEvidenceId)
    {
        ValidateEnvelopeStructure(envelope);
        if (!string.Equals(
                envelope.SchemaVersion,
                EvidenceEnvelopeFormat.CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new EvidenceIntegrityException(
                "Unsigned, legacy, or unsupported evidence schema was rejected.");
        }

        if (envelope.Evidence.Id != expectedEvidenceId)
            throw new EvidenceIntegrityException("Evidence ID does not match its storage key.");
        if (envelope.Evidence.Promotions.Count != 0)
        {
            throw new EvidenceIntegrityException(
                "Base evidence contains promotion records outside the signed event chain.");
        }

        VerifySeal(
            envelope.Seal,
            CreateExecutionPayload(
                envelope.Evidence,
                envelope.Seal.Algorithm,
                envelope.Seal.KeyId,
                envelope.Seal.SignedAt),
            "execution evidence");

        var previousSignature = envelope.Seal.Signature;
        for (var index = 0; index < envelope.PromotionEvents.Count; index++)
        {
            var promotionEvent = envelope.PromotionEvents[index];
            var expectedSequence = index + 1;
            if (promotionEvent.Sequence != expectedSequence)
            {
                throw new EvidenceIntegrityException(
                    $"Promotion event sequence {promotionEvent.Sequence} is invalid; expected {expectedSequence}.");
            }

            if (!FixedTimeTextEquals(promotionEvent.PreviousSignature, previousSignature))
            {
                throw new EvidenceIntegrityException(
                    $"Promotion event {expectedSequence} is disconnected from the signature chain.");
            }

            ValidatePromotion(envelope, promotionEvent.Promotion, validateDuplicate: false);
            VerifySeal(
                promotionEvent.Seal,
                CreatePromotionPayload(
                    envelope.Evidence.Id,
                    promotionEvent.Sequence,
                    promotionEvent.PreviousSignature,
                    promotionEvent.Promotion,
                    promotionEvent.Seal.Algorithm,
                    promotionEvent.Seal.KeyId,
                    promotionEvent.Seal.SignedAt),
                $"promotion event {expectedSequence}");
            previousSignature = promotionEvent.Seal.Signature;
        }

        var duplicatePromotion = envelope.PromotionEvents
            .GroupBy(item => item.Promotion.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePromotion is not null)
        {
            throw new EvidenceIntegrityException(
                $"Promotion event ID '{duplicatePromotion.Key}' occurs more than once.");
        }

        VerifySeal(
            envelope.ChainSeal,
            CreateChainPayload(
                envelope.Evidence.Id,
                envelope.PromotionEvents.Count,
                envelope.Seal.Signature,
                previousSignature,
                envelope.ChainSeal.Algorithm,
                envelope.ChainSeal.KeyId,
                envelope.ChainSeal.SignedAt),
            "evidence chain head");
    }

    public static string ContentHash(ExecutionEvidence evidence)
    {
        var canonical = CanonicalJson.Serialize(
            evidence,
            EvidenceEnvelopeFormat.SerializerOptions);
        return Hash(canonical);
    }

    public static bool PromotionEquals(
        CandidatePromotionEvidence left,
        CandidatePromotionEvidence right)
    {
        var leftPayload = CanonicalJson.Serialize(
            left,
            EvidenceEnvelopeFormat.SerializerOptions);
        var rightPayload = CanonicalJson.Serialize(
            right,
            EvidenceEnvelopeFormat.SerializerOptions);
        return CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(leftPayload),
            SHA256.HashData(rightPayload));
    }

    private SignedPromotionEvent CreatePromotionEvent(
        Guid executionEvidenceId,
        int sequence,
        string previousSignature,
        CandidatePromotionEvidence promotion)
    {
        var signedAt = DateTime.UtcNow;
        var seal = CreateSeal(CreatePromotionPayload(
            executionEvidenceId,
            sequence,
            previousSignature,
            promotion,
            _signatures.Algorithm,
            _signatures.KeyId,
            signedAt), signedAt);
        return new SignedPromotionEvent
        {
            Sequence = sequence,
            PreviousSignature = previousSignature,
            Promotion = promotion,
            Seal = seal
        };
    }

    private EvidenceSeal CreateSeal(object payload, DateTime signedAt)
    {
        var canonicalPayload = CanonicalJson.Serialize(
            payload,
            EvidenceEnvelopeFormat.SerializerOptions);
        return new EvidenceSeal
        {
            Algorithm = _signatures.Algorithm,
            KeyId = _signatures.KeyId,
            PayloadSha256 = Hash(canonicalPayload),
            Signature = Convert.ToBase64String(_signatures.Sign(canonicalPayload)),
            SignedAt = signedAt
        };
    }

    private EvidenceSeal CreateChainSeal(SignedExecutionEvidenceEnvelope envelope)
    {
        var signedAt = DateTime.UtcNow;
        var lastSignature = envelope.PromotionEvents.Count == 0
            ? envelope.Seal.Signature
            : envelope.PromotionEvents[^1].Seal.Signature;
        return CreateSeal(CreateChainPayload(
            envelope.Evidence.Id,
            envelope.PromotionEvents.Count,
            envelope.Seal.Signature,
            lastSignature,
            _signatures.Algorithm,
            _signatures.KeyId,
            signedAt), signedAt);
    }

    private static void ValidateEnvelopeStructure(SignedExecutionEvidenceEnvelope envelope)
    {
        if (envelope.Evidence is null ||
            envelope.Seal is null ||
            envelope.ChainSeal is null ||
            envelope.PromotionEvents is null ||
            envelope.PromotionEvents.Any(item =>
                item is null || item.Promotion is null || item.Seal is null))
        {
            throw new EvidenceIntegrityException("Evidence envelope is incomplete.");
        }

        ValidateEvidenceStructure(envelope.Evidence);
    }

    internal static void ValidateEvidenceStructure(ExecutionEvidence evidence)
    {
        if (evidence.TaskContract is null ||
            evidence.AgentRun is null ||
            evidence.AgentResult is null ||
            evidence.AgentAttempts is null ||
            evidence.BudgetUsage is null ||
            evidence.Baseline is null ||
            evidence.BaselineVerificationResults is null ||
            evidence.BaselineCommands is null ||
            evidence.ContextManifest is null ||
            evidence.CandidateChangeSet is null ||
            evidence.VerificationResults is null ||
            evidence.AcceptanceCriteriaResults is null ||
            evidence.CandidateCommands is null ||
            evidence.FinalDecision is null ||
            evidence.Promotions is null ||
            evidence.StateTransitions is null)
        {
            throw new EvidenceIntegrityException(
                "Execution evidence is incomplete and cannot be trusted.");
        }
    }

    private void VerifySeal(EvidenceSeal seal, object payload, string description)
    {
        if (string.IsNullOrWhiteSpace(seal.Algorithm) ||
            string.IsNullOrWhiteSpace(seal.KeyId) ||
            string.IsNullOrWhiteSpace(seal.PayloadSha256) ||
            string.IsNullOrWhiteSpace(seal.Signature) ||
            seal.SignedAt == default)
        {
            throw new EvidenceIntegrityException($"The {description} seal is incomplete.");
        }

        var canonicalPayload = CanonicalJson.Serialize(
            payload,
            EvidenceEnvelopeFormat.SerializerOptions);
        var calculatedHash = Hash(canonicalPayload);
        if (!FixedTimeTextEquals(calculatedHash, seal.PayloadSha256))
            throw new EvidenceIntegrityException($"The {description} payload hash is invalid.");

        byte[] signature;
        try
        {
            signature = Convert.FromBase64String(seal.Signature);
        }
        catch (FormatException ex)
        {
            throw new EvidenceIntegrityException(
                $"The {description} signature encoding is invalid.",
                ex);
        }

        if (!_signatures.Verify(
                seal.Algorithm,
                seal.KeyId,
                canonicalPayload,
                signature))
        {
            throw new EvidenceIntegrityException(
                $"The {description} signature is invalid or its key is not trusted.");
        }
    }

    private static void ValidatePromotion(
        SignedExecutionEvidenceEnvelope envelope,
        CandidatePromotionEvidence promotion,
        bool validateDuplicate = true)
    {
        if (promotion.Id == Guid.Empty)
            throw new EvidenceIntegrityException("Promotion evidence must have a non-empty ID.");
        if (promotion.ExecutionEvidenceId != envelope.Evidence.Id)
        {
            throw new EvidenceIntegrityException(
                "Promotion event references a different execution evidence ID.");
        }

        if (promotion.CandidateId != envelope.Evidence.CandidateChangeSet.Id)
            throw new EvidenceIntegrityException("Promotion event references a different candidate ID.");
        if (!string.Equals(
                promotion.BaselineCommit,
                envelope.Evidence.Baseline.Commit,
                StringComparison.Ordinal))
        {
            throw new EvidenceIntegrityException(
                "Promotion event references a different baseline commit.");
        }

        if (!FixedTimeTextEquals(
                promotion.DiffHash,
                envelope.Evidence.CandidateChangeSet.DiffHash))
        {
            throw new EvidenceIntegrityException("Promotion event references a different diff hash.");
        }

        if (validateDuplicate && envelope.PromotionEvents.Any(item => item.Promotion.Id == promotion.Id))
            throw new InvalidOperationException("Promotion evidence has already been recorded.");
    }

    private static object CreateExecutionPayload(
        ExecutionEvidence evidence,
        string algorithm,
        string keyId,
        DateTime signedAt) => new
        {
            SchemaVersion = EvidenceEnvelopeFormat.CurrentSchemaVersion,
            Kind = ExecutionPayloadKind,
            Algorithm = algorithm,
            KeyId = keyId,
            SignedAt = signedAt,
            Evidence = evidence
        };

    private static object CreatePromotionPayload(
        Guid executionEvidenceId,
        int sequence,
        string previousSignature,
        CandidatePromotionEvidence promotion,
        string algorithm,
        string keyId,
        DateTime signedAt) => new
        {
            SchemaVersion = EvidenceEnvelopeFormat.CurrentSchemaVersion,
            Kind = PromotionPayloadKind,
            Algorithm = algorithm,
            KeyId = keyId,
            SignedAt = signedAt,
            ExecutionEvidenceId = executionEvidenceId,
            Sequence = sequence,
            PreviousSignature = previousSignature,
            Promotion = promotion
        };

    private static object CreateChainPayload(
        Guid executionEvidenceId,
        int eventCount,
        string initialSignature,
        string lastSignature,
        string algorithm,
        string keyId,
        DateTime signedAt) => new
        {
            SchemaVersion = EvidenceEnvelopeFormat.CurrentSchemaVersion,
            Kind = ChainPayloadKind,
            Algorithm = algorithm,
            KeyId = keyId,
            SignedAt = signedAt,
            ExecutionEvidenceId = executionEvidenceId,
            EventCount = eventCount,
            InitialSignature = initialSignature,
            LastSignature = lastSignature
        };

    private static string Hash(ReadOnlySpan<byte> value)
    {
        var digest = SHA256.HashData(value);
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private static bool FixedTimeTextEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }
}
