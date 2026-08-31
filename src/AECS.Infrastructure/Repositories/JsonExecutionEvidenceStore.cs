using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Exceptions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Cryptography;

namespace AECS.Infrastructure.Repositories;

public sealed class JsonExecutionEvidenceStore : IExecutionEvidenceStore
{
    public const string CurrentSchemaVersion = "aecs.execution-evidence/v1";

    private const string ExecutionPayloadKind = "execution-evidence";
    private const string PromotionPayloadKind = "candidate-promotion";
    private const string ChainPayloadKind = "evidence-chain-head";
    private static readonly TimeSpan EvidenceLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly string _rootPath;
    private readonly string? _keyDirectoryPath;
    private readonly Lazy<IEvidenceSignatureService> _signatures;

    public JsonExecutionEvidenceStore(string rootPath)
        : this(rootPath, GetDefaultKeyDirectoryPath())
    {
    }

    public JsonExecutionEvidenceStore(string rootPath, string keyDirectoryPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
        _keyDirectoryPath = Path.GetFullPath(keyDirectoryPath);
        _signatures = new Lazy<IEvidenceSignatureService>(
            () => RsaEvidenceSignatureService.LoadOrCreate(_keyDirectoryPath),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public JsonExecutionEvidenceStore(
        string rootPath,
        IEvidenceSignatureService signatureService)
    {
        ArgumentNullException.ThrowIfNull(signatureService);
        _rootPath = Path.GetFullPath(rootPath);
        _signatures = new Lazy<IEvidenceSignatureService>(() => signatureService);
    }

    public static string GetDefaultRootPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("AECS_EVIDENCE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        return Path.Combine(GetAecsLocalDataRoot(), "evidence");
    }

    public static string GetDefaultKeyDirectoryPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("AECS_EVIDENCE_KEY_DIRECTORY");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return Path.GetFullPath(configuredPath);

        return Path.Combine(GetAecsLocalDataRoot(), "keys");
    }

    public void EnsureRepositoryIsolation(string repositoryPath)
    {
        var repositoryRoot = Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        EnsureOutsideRepository(_rootPath, repositoryRoot, "Evidence path");
        if (_keyDirectoryPath is not null)
            EnsureOutsideRepository(_keyDirectoryPath, repositoryRoot, "Evidence key directory");
    }

    public async Task<string> SaveAsync(
        ExecutionEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        ValidateEvidenceStructure(evidence);
        EnsureRepositoryIsolation(evidence.Baseline.RepositoryPath);
        if (evidence.Id == Guid.Empty)
            throw new EvidenceIntegrityException("Execution evidence must have a non-empty ID.");
        if (evidence.Promotions.Count != 0)
        {
            throw new EvidenceIntegrityException(
                "Initial execution evidence cannot contain mutable promotion records.");
        }

        Directory.CreateDirectory(_rootPath);
        var envelope = CreateEnvelope(evidence);
        var targetPath = GetEvidencePath(evidence.Id);
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";

        try
        {
            await WriteEnvelopeAsync(temporaryPath, envelope, cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: false);
            return targetPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<ExecutionEvidence?> LoadAsync(
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        var path = GetEvidencePath(evidenceId);
        if (!File.Exists(path))
            return null;

        var envelope = await ReadAndValidateEnvelopeAsync(
            path,
            evidenceId,
            cancellationToken);
        foreach (var promotionEvent in envelope.PromotionEvents)
            envelope.Evidence.Promotions.Add(promotionEvent.Promotion);
        return envelope.Evidence;
    }

    public async Task AppendPromotionAsync(
        Guid evidenceId,
        CandidatePromotionEvidence promotion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        var targetPath = GetEvidencePath(evidenceId);
        var writeLock = WriteLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(
                targetPath + ".lock",
                cancellationToken);
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("Execution evidence not found.", targetPath);

            var envelope = await ReadAndValidateEnvelopeAsync(
                targetPath,
                evidenceId,
                cancellationToken);
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

            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                await WriteEnvelopeAsync(temporaryPath, envelope, cancellationToken);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    private SignedExecutionEvidenceEnvelope CreateEnvelope(ExecutionEvidence evidence)
    {
        var signedAt = DateTime.UtcNow;
        var seal = CreateSeal(CreateExecutionPayload(
            evidence,
            _signatures.Value.Algorithm,
            _signatures.Value.KeyId,
            signedAt), signedAt);
        var envelope = new SignedExecutionEvidenceEnvelope
        {
            SchemaVersion = CurrentSchemaVersion,
            Evidence = evidence,
            Seal = seal
        };
        envelope.ChainSeal = CreateChainSeal(envelope);
        return envelope;
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
            _signatures.Value.Algorithm,
            _signatures.Value.KeyId,
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
        var canonicalPayload = CanonicalJson.Serialize(payload, SerializerOptions);
        return new EvidenceSeal
        {
            Algorithm = _signatures.Value.Algorithm,
            KeyId = _signatures.Value.KeyId,
            PayloadSha256 = Hash(canonicalPayload),
            Signature = Convert.ToBase64String(_signatures.Value.Sign(canonicalPayload)),
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
            _signatures.Value.Algorithm,
            _signatures.Value.KeyId,
            signedAt), signedAt);
    }

    private async Task<SignedExecutionEvidenceEnvelope> ReadAndValidateEnvelopeAsync(
        string path,
        Guid expectedEvidenceId,
        CancellationToken cancellationToken)
    {
        try
        {
            await using var stream = File.OpenRead(path);
            using var document = await JsonDocument.ParseAsync(
                stream,
                new JsonDocumentOptions
                {
                    AllowTrailingCommas = false,
                    CommentHandling = JsonCommentHandling.Disallow,
                    MaxDepth = SerializerOptions.MaxDepth
                },
                cancellationToken);
            CanonicalJson.RejectDuplicateProperties(document.RootElement);

            if (document.RootElement.ValueKind != JsonValueKind.Object ||
                !document.RootElement.TryGetProperty("schemaVersion", out var schemaVersion) ||
                schemaVersion.ValueKind != JsonValueKind.String ||
                !string.Equals(
                    schemaVersion.GetString(),
                    CurrentSchemaVersion,
                    StringComparison.Ordinal))
            {
                throw new EvidenceIntegrityException(
                    "Unsigned, legacy, or unsupported evidence schema was rejected.");
            }

            var envelope = document.RootElement.Deserialize<SignedExecutionEvidenceEnvelope>(
                    SerializerOptions)
                ?? throw new EvidenceIntegrityException("Evidence envelope is empty.");
            ValidateEnvelopeStructure(envelope);
            EnsureRepositoryIsolation(envelope.Evidence.Baseline.RepositoryPath);
            ValidateEnvelope(envelope, expectedEvidenceId);
            return envelope;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EvidenceIntegrityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            throw new EvidenceIntegrityException(
                "Evidence envelope is malformed or cannot be cryptographically verified.",
                ex);
        }
    }

    private void ValidateEnvelope(
        SignedExecutionEvidenceEnvelope envelope,
        Guid expectedEvidenceId)
    {
        if (!string.Equals(
                envelope.SchemaVersion,
                CurrentSchemaVersion,
                StringComparison.Ordinal))
        {
            throw new EvidenceIntegrityException(
                "Unsigned, legacy, or unsupported evidence schema was rejected.");
        }

        if (envelope.Evidence.Id != expectedEvidenceId)
            throw new EvidenceIntegrityException("Evidence ID does not match its file name.");
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

    private static void ValidateEvidenceStructure(ExecutionEvidence evidence)
    {
        if (evidence.TaskContract is null ||
            evidence.AgentRun is null ||
            evidence.AgentResult is null ||
            evidence.Baseline is null ||
            evidence.CandidateChangeSet is null ||
            evidence.VerificationResults is null ||
            evidence.FinalDecision is null ||
            evidence.Promotions is null)
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

        var canonicalPayload = CanonicalJson.Serialize(payload, SerializerOptions);
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

        if (!_signatures.Value.Verify(
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
            SchemaVersion = CurrentSchemaVersion,
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
            SchemaVersion = CurrentSchemaVersion,
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
            SchemaVersion = CurrentSchemaVersion,
            Kind = ChainPayloadKind,
            Algorithm = algorithm,
            KeyId = keyId,
            SignedAt = signedAt,
            ExecutionEvidenceId = executionEvidenceId,
            EventCount = eventCount,
            InitialSignature = initialSignature,
            LastSignature = lastSignature
        };

    private static async Task WriteEnvelopeAsync(
        string path,
        SignedExecutionEvidenceEnvelope envelope,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            envelope,
            SerializerOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task<FileStream> AcquireFileLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + EvidenceLockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private static string GetAecsLocalDataRoot()
    {
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.GetTempPath();
        return Path.Combine(localData, "AECS");
    }

    private static void EnsureOutsideRepository(
        string targetPath,
        string repositoryRoot,
        string description)
    {
        var resolvedTarget = Path.GetFullPath(targetPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repositoryPrefix = repositoryRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(resolvedTarget, repositoryRoot, comparison) ||
            resolvedTarget.StartsWith(repositoryPrefix, comparison))
        {
            throw new InvalidOperationException(
                $"{description} '{resolvedTarget}' must be outside target repository '{repositoryRoot}'.");
        }
    }

    private string GetEvidencePath(Guid evidenceId) =>
        Path.Combine(_rootPath, $"{evidenceId:N}.json");

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
