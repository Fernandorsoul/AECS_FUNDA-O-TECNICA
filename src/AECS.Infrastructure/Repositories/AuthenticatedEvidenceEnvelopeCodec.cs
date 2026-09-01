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
    private const string ReplayPayloadKind = "execution-replay";
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

        var chain = GetChainEvents(envelope);
        var sequence = chain.Count + 1;
        var previousSignature = chain.Count == 0
            ? envelope.Seal.Signature
            : chain[^1].Seal.Signature;
        envelope.PromotionEvents.Add(CreatePromotionEvent(
            envelope.Evidence.Id,
            sequence,
            previousSignature,
            promotion));
        envelope.ChainSeal = CreateChainSeal(envelope);
    }

    public void AppendReplay(
        SignedExecutionEvidenceEnvelope envelope,
        ExecutionReplayEvidence replay)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        ArgumentNullException.ThrowIfNull(replay);
        Validate(envelope, envelope.Evidence.Id);
        ValidateReplay(envelope, replay);

        var chain = GetChainEvents(envelope);
        var sequence = chain.Count + 1;
        var previousSignature = chain.Count == 0
            ? envelope.Seal.Signature
            : chain[^1].Seal.Signature;
        envelope.ReplayEvents.Add(CreateReplayEvent(
            envelope.Evidence.Id,
            sequence,
            previousSignature,
            replay));
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
        var chain = GetChainEvents(envelope);
        for (var index = 0; index < chain.Count; index++)
        {
            var chainEvent = chain[index];
            var expectedSequence = index + 1;
            if (chainEvent.Sequence != expectedSequence)
            {
                throw new EvidenceIntegrityException(
                    $"Evidence event sequence {chainEvent.Sequence} is invalid; expected {expectedSequence}.");
            }

            if (!FixedTimeTextEquals(chainEvent.PreviousSignature, previousSignature))
            {
                throw new EvidenceIntegrityException(
                    $"Evidence event {expectedSequence} is disconnected from the signature chain.");
            }

            if (chainEvent.Promotion is not null)
            {
                ValidatePromotion(envelope, chainEvent.Promotion, validateDuplicate: false);
                VerifySeal(
                    chainEvent.Seal,
                    CreatePromotionPayload(
                        envelope.Evidence.Id,
                        chainEvent.Sequence,
                        chainEvent.PreviousSignature,
                        chainEvent.Promotion,
                        chainEvent.Seal.Algorithm,
                        chainEvent.Seal.KeyId,
                        chainEvent.Seal.SignedAt),
                    $"promotion event {expectedSequence}");
            }
            else
            {
                ValidateReplay(envelope, chainEvent.Replay!, validateDuplicate: false);
                VerifySeal(
                    chainEvent.Seal,
                    CreateReplayPayload(
                        envelope.Evidence.Id,
                        chainEvent.Sequence,
                        chainEvent.PreviousSignature,
                        chainEvent.Replay!,
                        chainEvent.Seal.Algorithm,
                        chainEvent.Seal.KeyId,
                        chainEvent.Seal.SignedAt),
                    $"replay event {expectedSequence}");
            }

            previousSignature = chainEvent.Seal.Signature;
        }

        var duplicatePromotion = envelope.PromotionEvents
            .GroupBy(item => item.Promotion.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicatePromotion is not null)
        {
            throw new EvidenceIntegrityException(
                $"Promotion event ID '{duplicatePromotion.Key}' occurs more than once.");
        }

        var duplicateReplay = envelope.ReplayEvents
            .GroupBy(item => item.Replay.Id)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateReplay is not null)
        {
            throw new EvidenceIntegrityException(
                $"Replay event ID '{duplicateReplay.Key}' occurs more than once.");
        }

        VerifySeal(
            envelope.ChainSeal,
            CreateChainPayload(
                envelope.Evidence.Id,
                chain.Count,
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

    public static bool ReplayEquals(
        ExecutionReplayEvidence left,
        ExecutionReplayEvidence right)
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

    private SignedReplayEvent CreateReplayEvent(
        Guid executionEvidenceId,
        int sequence,
        string previousSignature,
        ExecutionReplayEvidence replay)
    {
        var signedAt = DateTime.UtcNow;
        var seal = CreateSeal(CreateReplayPayload(
            executionEvidenceId,
            sequence,
            previousSignature,
            replay,
            _signatures.Algorithm,
            _signatures.KeyId,
            signedAt), signedAt);
        return new SignedReplayEvent
        {
            Sequence = sequence,
            PreviousSignature = previousSignature,
            Replay = replay,
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
        var chain = GetChainEvents(envelope);
        var lastSignature = chain.Count == 0
            ? envelope.Seal.Signature
            : chain[^1].Seal.Signature;
        return CreateSeal(CreateChainPayload(
            envelope.Evidence.Id,
            chain.Count,
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
            envelope.ReplayEvents is null ||
            envelope.PromotionEvents.Any(item =>
                item is null || item.Promotion is null || item.Seal is null) ||
            envelope.ReplayEvents.Any(item =>
                item is null || item.Replay is null || item.Seal is null) ||
            !IsStrictlyIncreasing(envelope.PromotionEvents.Select(item => item.Sequence)) ||
            !IsStrictlyIncreasing(envelope.ReplayEvents.Select(item => item.Sequence)))
        {
            throw new EvidenceIntegrityException(
                "Evidence envelope is incomplete or its event sequence is invalid.");
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

        var securityScans = evidence.BaselineVerificationResults
            .Concat(evidence.VerificationResults)
            .Where(result => result.SecurityScan is not null)
            .Select(result => result.SecurityScan!);
        if (securityScans.Any(scan =>
                scan.SchemaVersion != SecurityScanSchema.EvidenceVersion ||
                string.IsNullOrWhiteSpace(scan.PolicyVersion) ||
                string.IsNullOrWhiteSpace(scan.PolicyHash) ||
                string.IsNullOrWhiteSpace(scan.ScannerVersion) ||
                string.IsNullOrWhiteSpace(scan.VulnerabilityDatabaseVersion) ||
                scan.Scanners is null ||
                scan.Findings is null))
        {
            throw new EvidenceIntegrityException(
                "Security scan evidence is incomplete or has an unsupported schema.");
        }

        var testSuites = evidence.BaselineVerificationResults
            .Concat(evidence.VerificationResults)
            .Where(result => result.TestSuite is not null)
            .Select(result => result.TestSuite!)
            .ToList();
        var commandIds = evidence.BaselineCommands
            .Concat(evidence.CandidateCommands)
            .Select(command => command.Id)
            .ToHashSet();
        if (testSuites.Any(suite =>
                suite.SchemaVersion != TestSuiteSchema.EvidenceVersion ||
                suite.ProfileVersion != TestSuiteSchema.ProfileVersion ||
                suite.Mode == TestGateMode.Disabled ||
                string.IsNullOrWhiteSpace(suite.Target) ||
                suite.Arguments is null ||
                suite.Discovered < 0 ||
                suite.Executed < 0 ||
                suite.Passed < 0 ||
                suite.Failed < 0 ||
                suite.Skipped < 0 ||
                suite.Executed > suite.Discovered ||
                suite.Passed + suite.Failed > suite.Executed ||
                suite.CommandEvidenceId != Guid.Empty &&
                !commandIds.Contains(suite.CommandEvidenceId)))
        {
            throw new EvidenceIntegrityException(
                "Test suite evidence is incomplete, inconsistent, or has an unsupported schema.");
        }

        var snapshot = evidence.RepositorySnapshot;
        if (snapshot is not null &&
            (snapshot.SchemaVersion != RepositorySnapshotSchema.SnapshotVersion ||
             snapshot.StrategyVersion != RepositorySnapshotSchema.DiscoveryStrategy ||
             snapshot.ProfileVersion != RepositorySnapshotSchema.ProfileVersion ||
             !string.Equals(snapshot.BaselineCommit, evidence.Baseline.Commit, StringComparison.Ordinal) ||
             !string.Equals(snapshot.TaskContractId, evidence.TaskContract.Id, StringComparison.Ordinal) ||
             snapshot.ExcludedEntryCount < 0 ||
             snapshot.ExcludedDirectories is null ||
             snapshot.Tools is null ||
             snapshot.Files is null ||
             snapshot.Solutions is null ||
             snapshot.Projects is null ||
             snapshot.Languages is null ||
             snapshot.Frameworks is null ||
             snapshot.Manifests is null ||
             snapshot.Packages is null ||
             snapshot.Entrypoints is null ||
             snapshot.TestSuites is null ||
             snapshot.Relationships is null ||
             !IsSha256(snapshot.SnapshotHash) ||
             !IsSha256(snapshot.ConfigurationHash) ||
             !FixedTimeTextEquals(
                 snapshot.ConfigurationHash,
                 RepositorySnapshotFingerprint.CreateConfiguration(
                     snapshot.ProfileVersion,
                     snapshot.ExcludedDirectories)) ||
             !FixedTimeTextEquals(
                 snapshot.SnapshotHash,
                 RepositorySnapshotFingerprint.Create(snapshot)) ||
             snapshot.Files.Any(file =>
                 !IsSafeRepositoryPath(file.Path) ||
                 !IsGitHash(file.Hash) ||
                 file.Size < 0) ||
             snapshot.Files.Select(file => file.Path).Distinct(StringComparer.Ordinal).Count() !=
                 snapshot.Files.Count ||
             snapshot.Solutions.Any(solution =>
                 !IsSafeRepositoryPath(solution.Path) ||
                 !IsGitHash(solution.Hash) ||
                 solution.Projects is null ||
                 solution.Projects.Any(project => !IsSafeRepositoryPath(project))) ||
             snapshot.Projects.Any(project =>
                 !IsSafeRepositoryPath(project.Path) ||
                 !IsGitHash(project.Hash) ||
                 project.Frameworks is null ||
                 project.ProjectReferences is null ||
                 project.PackageReferences is null ||
                 project.ProjectReferences.Any(reference => !IsSafeRepositoryPath(reference))) ||
             snapshot.Languages.Any(language =>
                 string.IsNullOrWhiteSpace(language.Name) || language.FileCount <= 0) ||
             snapshot.Frameworks.Any(string.IsNullOrWhiteSpace) ||
             snapshot.Manifests.Any(manifest =>
                 !IsSafeRepositoryPath(manifest.Path) || !IsGitHash(manifest.Hash)) ||
             snapshot.Packages.Any(package =>
                 string.IsNullOrWhiteSpace(package.Ecosystem) ||
                 string.IsNullOrWhiteSpace(package.Name) ||
                 !IsSafeRepositoryPath(package.SourcePath)) ||
             snapshot.Entrypoints.Any(entrypoint =>
                 !IsSafeRepositoryPath(entrypoint.Path) ||
                 !string.IsNullOrEmpty(entrypoint.ProjectPath) &&
                 !IsSafeRepositoryPath(entrypoint.ProjectPath)) ||
             snapshot.TestSuites.Any(suite =>
                 string.IsNullOrWhiteSpace(suite.Name) ||
                 string.IsNullOrWhiteSpace(suite.Category) ||
                 !string.IsNullOrEmpty(suite.Target) && !IsSafeRepositoryPath(suite.Target)) ||
             snapshot.Relationships.Any(relationship =>
                 !IsSafeRepositoryPath(relationship.From) ||
                 !IsSafeRepositoryPath(relationship.To) ||
                 string.IsNullOrWhiteSpace(relationship.Kind))))
        {
            throw new EvidenceIntegrityException(
                "Repository snapshot is incomplete, inconsistent, or has an unsupported schema.");
        }

        var symbolGraph = evidence.CSharpSymbolGraph;
        var contextClaimsRoslyn = string.Equals(
            evidence.ContextManifest.SemanticIndex,
            "roslyn-symbol-graph",
            StringComparison.Ordinal);
        var contextHasGraphHash = !string.IsNullOrEmpty(
            evidence.ContextManifest.SymbolGraphHash);
        if (contextClaimsRoslyn != contextHasGraphHash)
        {
            throw new EvidenceIntegrityException(
                "Context manifest semantic authority and C# symbol graph hash are inconsistent.");
        }
        if (symbolGraph is null && contextHasGraphHash)
        {
            throw new EvidenceIntegrityException(
                "Context manifest references a C# symbol graph that is absent from the evidence.");
        }
        if (symbolGraph is not null)
        {
            var nodeIds = symbolGraph.Nodes?.Select(node => node.Id).ToHashSet(
                StringComparer.Ordinal) ?? [];
            var snapshotFiles = snapshot?.Files.Select(file => file.Path).ToHashSet(
                StringComparer.Ordinal) ?? [];
            var snapshotProjects = snapshot?.Projects.Select(project => project.Path).ToHashSet(
                StringComparer.Ordinal) ?? [];
            if (symbolGraph.SchemaVersion != CSharpSymbolGraphSchema.GraphVersion ||
                symbolGraph.StrategyVersion != CSharpSymbolGraphSchema.StrategyVersion ||
                snapshot is null ||
                !FixedTimeTextEquals(
                    symbolGraph.RepositorySnapshotHash,
                    snapshot.SnapshotHash) ||
                !string.Equals(
                    symbolGraph.BaselineCommit,
                    evidence.Baseline.Commit,
                    StringComparison.Ordinal) ||
                !IsSha256(symbolGraph.GraphHash) ||
                string.IsNullOrWhiteSpace(symbolGraph.CompilerVersion) ||
                string.IsNullOrWhiteSpace(symbolGraph.MsBuildVersion) ||
                string.IsNullOrWhiteSpace(symbolGraph.SdkVersion) ||
                symbolGraph.Limits is null ||
                symbolGraph.GlobalProperties is null ||
                symbolGraph.Projects is null ||
                symbolGraph.Nodes is null ||
                symbolGraph.Edges is null ||
                symbolGraph.Diagnostics is null ||
                !ValidSymbolGraphLimits(symbolGraph.Limits) ||
                !FixedTimeTextEquals(
                    symbolGraph.GraphHash,
                    CSharpSymbolGraphFingerprint.Create(symbolGraph)) ||
                symbolGraph.Projects.Count > symbolGraph.Limits.MaxProjects ||
                symbolGraph.Nodes.Count > symbolGraph.Limits.MaxNodes ||
                symbolGraph.Edges.Count > symbolGraph.Limits.MaxEdges ||
                symbolGraph.Diagnostics.Count > symbolGraph.Limits.MaxDiagnostics ||
                symbolGraph.Nodes.Count(node => node.Kind == "file") >
                    symbolGraph.Limits.MaxDocuments ||
                symbolGraph.Projects.Select(project => project.Id)
                    .Distinct(StringComparer.Ordinal).Count() != symbolGraph.Projects.Count ||
                symbolGraph.Projects.Any(project =>
                    project.Id != CSharpSymbolGraphFingerprint.StableProjectId(project.Path) ||
                    !IsSha256(project.Hash) ||
                    !IsSafeRepositoryPath(project.Path) ||
                    !snapshotProjects.Contains(project.Path) ||
                    project.TargetFrameworks is null ||
                    project.PreprocessorSymbols is null ||
                    project.ProjectReferences is null ||
                    project.ProjectReferences.Any(reference =>
                        !IsSafeRepositoryPath(reference) ||
                        !snapshotProjects.Contains(reference)) ||
                    !FixedTimeTextEquals(
                        project.Hash,
                        CSharpSymbolGraphFingerprint.CreateProject(project))) ||
                symbolGraph.Nodes.Select(node => node.Id)
                    .Distinct(StringComparer.Ordinal).Count() != symbolGraph.Nodes.Count ||
                symbolGraph.Nodes.Any(node =>
                    node.Id != CSharpSymbolGraphFingerprint.StableNodeId(node) ||
                    !IsSha256(node.Hash) ||
                    node.Kind is not ("project" or "file" or "namespace" or "type" or "member") ||
                    node.FilePaths is null ||
                    node.Modifiers is null ||
                    node.Arity < 0 ||
                    (!string.IsNullOrEmpty(node.ProjectPath) &&
                        (!IsSafeRepositoryPath(node.ProjectPath) ||
                            !snapshotProjects.Contains(node.ProjectPath))) ||
                    node.FilePaths.Any(path =>
                        !IsSafeRepositoryPath(path) || !snapshotFiles.Contains(path)) ||
                    (!string.IsNullOrEmpty(node.SourceHash) && !IsGitHash(node.SourceHash)) ||
                    (!string.IsNullOrEmpty(node.ContainingNodeId) &&
                        !nodeIds.Contains(node.ContainingNodeId)) ||
                    !FixedTimeTextEquals(
                        node.Hash,
                        CSharpSymbolGraphFingerprint.CreateNode(node))) ||
                symbolGraph.Edges.Select(edge => edge.Id)
                    .Distinct(StringComparer.Ordinal).Count() != symbolGraph.Edges.Count ||
                symbolGraph.Edges.Any(edge =>
                    !edge.Id.StartsWith("CSE-", StringComparison.Ordinal) ||
                    !IsSha256(edge.Hash) ||
                    edge.Kind is not ("contains" or "declares" or "inherits" or "implements" or
                        "project-reference" or "references") ||
                    !nodeIds.Contains(edge.FromNodeId) ||
                    !nodeIds.Contains(edge.ToNodeId) ||
                    edge.Id != CSharpSymbolGraphFingerprint.StableEdgeId(
                        edge.Kind,
                        edge.FromNodeId,
                        edge.ToNodeId) ||
                    !FixedTimeTextEquals(
                        edge.Hash,
                        CSharpSymbolGraphFingerprint.CreateEdge(edge))) ||
                symbolGraph.Diagnostics.Select(diagnostic => diagnostic.Id)
                    .Distinct(StringComparer.Ordinal).Count() != symbolGraph.Diagnostics.Count ||
                symbolGraph.Diagnostics.Any(diagnostic =>
                    diagnostic.Id != CSharpSymbolGraphFingerprint.StableDiagnosticId(diagnostic) ||
                    string.IsNullOrWhiteSpace(diagnostic.Source) ||
                    string.IsNullOrWhiteSpace(diagnostic.Severity) ||
                    string.IsNullOrWhiteSpace(diagnostic.Code) ||
                    diagnostic.Line < 0 ||
                    diagnostic.Column < 0 ||
                    (!string.IsNullOrEmpty(diagnostic.ProjectPath) &&
                        !IsSafeRepositoryPath(diagnostic.ProjectPath)) ||
                    (!string.IsNullOrEmpty(diagnostic.FilePath) &&
                        !IsSafeRepositoryPath(diagnostic.FilePath))) ||
                !string.IsNullOrEmpty(evidence.ContextManifest.SymbolGraphHash) &&
                    (!symbolGraph.LoadSucceeded ||
                     !string.Equals(
                         evidence.ContextManifest.SemanticIndex,
                         "roslyn-symbol-graph",
                         StringComparison.Ordinal) ||
                     !FixedTimeTextEquals(
                         evidence.ContextManifest.SymbolGraphHash,
                         symbolGraph.GraphHash)))
            {
                throw new EvidenceIntegrityException(
                    "C# symbol graph is incomplete, inconsistent, or has an unsupported schema.");
            }
        }
    }

    private static bool ValidSymbolGraphLimits(CSharpSymbolGraphLimits limits) =>
        limits.MaxDurationSeconds > 0 &&
        limits.MaxEstimatedMemoryBytes > 0 &&
        limits.MaxProjects > 0 &&
        limits.MaxDocuments > 0 &&
        limits.MaxNodes > 0 &&
        limits.MaxEdges > 0 &&
        limits.MaxDiagnostics > 0;

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

    private static void ValidateReplay(
        SignedExecutionEvidenceEnvelope envelope,
        ExecutionReplayEvidence replay,
        bool validateDuplicate = true)
    {
        if (replay.Id == Guid.Empty)
            throw new EvidenceIntegrityException("Replay evidence must have a non-empty ID.");
        if (replay.ExecutionEvidenceId != envelope.Evidence.Id)
            throw new EvidenceIntegrityException("Replay event references a different evidence ID.");
        if (replay.CandidateId != envelope.Evidence.CandidateChangeSet.Id)
            throw new EvidenceIntegrityException("Replay event references a different candidate ID.");
        if (!string.Equals(
                replay.BaselineCommit,
                envelope.Evidence.Baseline.Commit,
                StringComparison.Ordinal))
        {
            throw new EvidenceIntegrityException(
                "Replay event references a different baseline commit.");
        }

        if (!FixedTimeTextEquals(
                replay.ExpectedDiffHash,
                envelope.Evidence.CandidateChangeSet.DiffHash))
        {
            throw new EvidenceIntegrityException(
                "Replay event references a different expected diff hash.");
        }

        if (replay.Tools is null ||
            replay.Commands is null ||
            replay.Gates is null ||
            replay.AcceptanceCriteria is null ||
            string.IsNullOrWhiteSpace(replay.RepositoryPath) ||
            string.IsNullOrWhiteSpace(replay.RequestedRepositoryPath) ||
            replay.StartedAt == default ||
            replay.FinishedAt == default ||
            replay.FinishedAt < replay.StartedAt)
        {
            throw new EvidenceIntegrityException("Replay event is incomplete.");
        }

        var replayRepository = Path.GetFullPath(replay.RepositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var evidenceRepository = Path.GetFullPath(envelope.Evidence.Baseline.RepositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!string.Equals(
                replayRepository,
                evidenceRepository,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new EvidenceIntegrityException(
                "Replay event references a different repository path.");
        }

        if (replay.Outcome == ExecutionReplayOutcome.Reproduced &&
            !FixedTimeTextEquals(replay.ActualDiffHash, replay.ExpectedDiffHash))
        {
            throw new EvidenceIntegrityException(
                "Successful replay event does not reproduce the expected diff hash.");
        }

        var expectedSnapshot = envelope.Evidence.RepositorySnapshot;
        if (expectedSnapshot is not null &&
            !FixedTimeTextEquals(
                replay.ExpectedRepositorySnapshotHash ?? string.Empty,
                expectedSnapshot.SnapshotHash))
        {
            throw new EvidenceIntegrityException(
                "Replay event references a different repository snapshot.");
        }
        if (replay.Outcome == ExecutionReplayOutcome.Reproduced &&
            expectedSnapshot is not null &&
            (!FixedTimeTextEquals(
                 replay.ActualRepositorySnapshotHash ?? string.Empty,
                 expectedSnapshot.SnapshotHash) ||
             replay.RepositorySnapshotDiff?.HasChanges != false))
        {
            throw new EvidenceIntegrityException(
                "Successful replay event does not reproduce the repository snapshot.");
        }

        var expectedSymbolGraph = envelope.Evidence.CSharpSymbolGraph;
        if (expectedSymbolGraph is not null &&
            !FixedTimeTextEquals(
                replay.ExpectedCSharpSymbolGraphHash ?? string.Empty,
                expectedSymbolGraph.GraphHash))
        {
            throw new EvidenceIntegrityException(
                "Replay event references a different C# symbol graph.");
        }
        if (replay.Outcome == ExecutionReplayOutcome.Reproduced &&
            expectedSymbolGraph is not null &&
            !FixedTimeTextEquals(
                replay.ActualCSharpSymbolGraphHash ?? string.Empty,
                expectedSymbolGraph.GraphHash))
        {
            throw new EvidenceIntegrityException(
                "Successful replay event does not reproduce the C# symbol graph.");
        }

        if (validateDuplicate && envelope.ReplayEvents.Any(item => item.Replay.Id == replay.Id))
            throw new InvalidOperationException("Replay evidence has already been recorded.");
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

    private static object CreateReplayPayload(
        Guid executionEvidenceId,
        int sequence,
        string previousSignature,
        ExecutionReplayEvidence replay,
        string algorithm,
        string keyId,
        DateTime signedAt) => new
        {
            SchemaVersion = EvidenceEnvelopeFormat.CurrentSchemaVersion,
            Kind = ReplayPayloadKind,
            Algorithm = algorithm,
            KeyId = keyId,
            SignedAt = signedAt,
            ExecutionEvidenceId = executionEvidenceId,
            Sequence = sequence,
            PreviousSignature = previousSignature,
            Replay = replay
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

    private static bool IsSha256(string value) =>
        value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => Uri.IsHexDigit(character) && !char.IsUpper(character));

    private static bool IsGitHash(string value) =>
        value.StartsWith("git:", StringComparison.Ordinal) &&
        value.Length is 44 or 68 &&
        value[4..].All(Uri.IsHexDigit);

    private static bool IsSafeRepositoryPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || Path.IsPathRooted(value) ||
            value.StartsWith('/') || value.Contains('\\'))
        {
            return false;
        }
        var segments = value.Split('/');
        return segments.All(segment => segment is not ("" or "." or "..")) &&
            !segments.Any(segment => segment.Equals(".git", StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsStrictlyIncreasing(IEnumerable<int> values)
    {
        var previous = 0;
        foreach (var value in values)
        {
            if (value <= previous)
                return false;
            previous = value;
        }

        return true;
    }

    private static List<ChainEvent> GetChainEvents(SignedExecutionEvidenceEnvelope envelope) =>
        envelope.PromotionEvents
            .Select(item => new ChainEvent(
                item.Sequence,
                item.PreviousSignature,
                item.Seal,
                item.Promotion,
                null))
            .Concat(envelope.ReplayEvents.Select(item => new ChainEvent(
                item.Sequence,
                item.PreviousSignature,
                item.Seal,
                null,
                item.Replay)))
            .OrderBy(item => item.Sequence)
            .ToList();

    private sealed record ChainEvent(
        int Sequence,
        string PreviousSignature,
        EvidenceSeal Seal,
        CandidatePromotionEvidence? Promotion,
        ExecutionReplayEvidence? Replay);
}
