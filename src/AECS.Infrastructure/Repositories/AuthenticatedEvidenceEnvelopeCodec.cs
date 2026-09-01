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

        ValidateSemanticEvidence(evidence);
        ValidateHistoricalEvidence(evidence);

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
        ValidateContextManifest(evidence, snapshot, symbolGraph);
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
                !CSharpSymbolGraphSchema.IsSupportedStrategyVersion(
                    symbolGraph.StrategyVersion) ||
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
                        "project-reference" or "references" or "constructs") ||
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

    private static void ValidateSemanticEvidence(ExecutionEvidence evidence)
    {
        var semanticResults = evidence.BaselineVerificationResults
            .Concat(evidence.VerificationResults)
            .Where(result => result.Semantic is not null)
            .Select(result => result.Semantic!)
            .ToList();
        if (semanticResults.Count == 0)
            return;

        var snapshot = evidence.RepositorySnapshot;
        var symbolGraph = evidence.CSharpSymbolGraph;
        var changedFiles = evidence.CandidateChangeSet.ChangedFiles
            .Select(path => path.Replace('\\', '/').Trim())
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var invalid = snapshot is null ||
            symbolGraph is null ||
            semanticResults.Any(semantic =>
                semantic.SchemaVersion != SemanticVerificationEvidenceSchema.Version ||
                !string.Equals(
                    semantic.BaselineCommit,
                    evidence.Baseline.Commit,
                    StringComparison.Ordinal) ||
                !FixedTimeTextEquals(
                    semantic.BaselineSnapshotHash,
                    snapshot.SnapshotHash) ||
                !FixedTimeTextEquals(
                    semantic.BaselineGraphHash,
                    symbolGraph.GraphHash) ||
                !IsSha256(semantic.CandidateSnapshotHash) ||
                !IsSha256(semantic.CandidateGraphHash) ||
                semantic.ImpactedFiles is null ||
                semantic.Findings is null ||
                semantic.ImpactedFiles.Any(path =>
                    !IsSafeRepositoryPath(path) || !changedFiles.Contains(path)) ||
                semantic.ImpactedFiles.Distinct(StringComparer.OrdinalIgnoreCase).Count() !=
                    semantic.ImpactedFiles.Count ||
                semantic.ImpactedFiles.ToHashSet(StringComparer.OrdinalIgnoreCase)
                    .SetEquals(changedFiles) == false ||
                semantic.Findings.Any(finding =>
                    string.IsNullOrWhiteSpace(finding.RuleId) ||
                    string.IsNullOrWhiteSpace(finding.SymbolId) ||
                    string.IsNullOrWhiteSpace(finding.Symbol) ||
                    !IsSafeRepositoryPath(finding.FilePath) ||
                    finding.Severity is not ("Info" or "Warning" or "Error" or "Critical") ||
                    string.IsNullOrWhiteSpace(finding.Baseline) ||
                    !string.Equals(
                        finding.Baseline,
                        semantic.BaselineCommit,
                        StringComparison.Ordinal) ||
                    string.IsNullOrWhiteSpace(finding.Justification)));
        if (invalid)
        {
            throw new EvidenceIntegrityException(
                "Semantic verification evidence is incomplete, inconsistent, or has an unsupported schema.");
        }
    }

    private static void ValidateHistoricalEvidence(ExecutionEvidence evidence)
    {
        var historical = evidence.BaselineVerificationResults
            .Concat(evidence.VerificationResults)
            .Where(result => result.Historical is not null)
            .Select(result => result.Historical!)
            .ToList();
        if (historical.Any(item => !ValidHistoricalEvidence(item)))
        {
            throw new EvidenceIntegrityException(
                "Historical decision verification evidence is incomplete, inconsistent, or has an unsupported schema.");
        }
    }

    private static bool ValidHistoricalEvidence(
        HistoricalDecisionVerificationEvidence evidence)
    {
        if (evidence.SchemaVersion !=
                HistoricalDecisionSchema.VerificationEvidenceVersion ||
            !Enum.IsDefined(evidence.Status) ||
            evidence.EvaluatedAt.Kind != DateTimeKind.Utc ||
            string.IsNullOrWhiteSpace(evidence.Message) ||
            evidence.Decisions is null ||
            evidence.Suppressions is null ||
            evidence.Conflicts is null ||
            evidence.Status == HistoricalDecisionSelectionStatus.Selected &&
                evidence.Decisions.Count == 0 ||
            evidence.Status == HistoricalDecisionSelectionStatus.Ambiguous &&
                (evidence.Decisions.Count == 0 || evidence.Suppressions.Count != 0) ||
            evidence.Status is HistoricalDecisionSelectionStatus.NoHistory or
                HistoricalDecisionSelectionStatus.Unavailable &&
                (evidence.Decisions.Count != 0 || evidence.Suppressions.Count != 0 ||
                 evidence.Conflicts.Count != 0) ||
            evidence.Status != HistoricalDecisionSelectionStatus.Selected &&
                evidence.Conflicts.Count != 0)
        {
            return false;
        }

        try
        {
            foreach (var decision in evidence.Decisions)
                HistoricalDecisionContract.Validate(decision);
            foreach (var suppression in evidence.Suppressions)
                HistoricalDecisionContract.Validate(suppression);
        }
        catch (InvalidOperationException)
        {
            return false;
        }

        if (evidence.Decisions.Select(decision => $"{decision.Id}\n{decision.Version}")
                .Distinct(StringComparer.Ordinal).Count() != evidence.Decisions.Count ||
            evidence.Suppressions.Select(suppression =>
                    $"{suppression.Id}\n{suppression.Version}")
                .Distinct(StringComparer.Ordinal).Count() != evidence.Suppressions.Count)
        {
            return false;
        }

        var decisions = evidence.Decisions.ToDictionary(
            decision => $"{decision.Id}\n{decision.Version}",
            StringComparer.Ordinal);
        var suppressions = evidence.Suppressions.ToDictionary(
            suppression => $"{suppression.Id}\n{suppression.Version}",
            StringComparer.Ordinal);
        if (decisions.Values.Any(decision =>
                decision.Review.Status != HistoricalDecisionReviewStatus.Approved ||
                decision.ValidFrom > evidence.EvaluatedAt ||
                decision.ValidUntil is { } validUntil && validUntil <= evidence.EvaluatedAt) ||
            suppressions.Values.Any(suppression =>
                !decisions.ContainsKey(
                    $"{suppression.DecisionId}\n{suppression.DecisionVersion}") ||
                suppression.ExpiresAt <= evidence.EvaluatedAt))
        {
            return false;
        }

        return evidence.Conflicts.All(conflict =>
        {
            if (!decisions.TryGetValue(
                    $"{conflict.DecisionId}\n{conflict.DecisionVersion}",
                    out var decision))
            {
                return false;
            }
            var valid = !string.IsNullOrWhiteSpace(conflict.RuleId) &&
                conflict.RuleId.StartsWith("EB005-", StringComparison.Ordinal) &&
                conflict.RuleId.Equals(
                    $"EB005-{decision.Type.ToString().ToUpperInvariant()}-CONFLICT",
                    StringComparison.Ordinal) &&
                string.Equals(conflict.Source, decision.Source, StringComparison.Ordinal) &&
                string.Equals(
                    conflict.SourceVersion,
                    decision.SourceVersion,
                    StringComparison.Ordinal) &&
                string.Equals(conflict.SourceHash, decision.SourceHash, StringComparison.Ordinal) &&
                string.Equals(conflict.Authority, decision.Authority, StringComparison.Ordinal) &&
                !string.IsNullOrWhiteSpace(conflict.SymbolId) &&
                !string.IsNullOrWhiteSpace(conflict.Symbol) &&
                IsSafeRepositoryPath(conflict.FilePath) &&
                conflict.Severity is "Warning" or "Error" &&
                decision.ProhibitedPatterns.Concat(decision.RequiredPatterns).Any(pattern =>
                    conflict.Pattern.Equals(
                        $"{pattern.Kind}:{pattern.Value}",
                        StringComparison.Ordinal)) &&
                conflict.Severity ==
                    (decision.Enforcement == HistoricalDecisionEnforcement.Blocking
                        ? "Error"
                        : "Warning") &&
                !string.IsNullOrWhiteSpace(conflict.Justification);
            if (!valid)
                return false;
            if (!conflict.Suppressed)
            {
                return string.IsNullOrEmpty(conflict.SuppressionId) &&
                    conflict.SuppressionVersion is null;
            }
            return conflict.SuppressionVersion is { } version &&
                suppressions.TryGetValue(
                    $"{conflict.SuppressionId}\n{version}",
                    out var suppression) &&
                suppression.DecisionId.Equals(conflict.DecisionId, StringComparison.Ordinal) &&
                suppression.DecisionVersion == conflict.DecisionVersion &&
                (string.IsNullOrEmpty(suppression.SymbolId) ||
                 suppression.SymbolId.Equals(conflict.SymbolId, StringComparison.Ordinal)) &&
                (string.IsNullOrEmpty(suppression.FilePath) ||
                 suppression.FilePath.Equals(conflict.FilePath, StringComparison.OrdinalIgnoreCase));
        });
    }

    private static bool ValidSymbolGraphLimits(CSharpSymbolGraphLimits limits) =>
        limits.MaxDurationSeconds > 0 &&
        limits.MaxEstimatedMemoryBytes > 0 &&
        limits.MaxProjects > 0 &&
        limits.MaxDocuments > 0 &&
        limits.MaxNodes > 0 &&
        limits.MaxEdges > 0 &&
        limits.MaxDiagnostics > 0;

    private static void ValidateContextManifest(
        ExecutionEvidence evidence,
        RepositorySnapshot? snapshot,
        CSharpSymbolGraph? symbolGraph)
    {
        var manifest = evidence.ContextManifest;
        if (manifest.SchemaVersion == ContextManifestSchema.LegacyVersion)
            return;
        if (manifest.SchemaVersion != ContextManifestSchema.CurrentVersion)
        {
            throw new EvidenceIntegrityException(
                "Context manifest has an unsupported schema.");
        }

        var includedSelections = manifest.Selections?
            .Where(selection => selection.Decision is "included" or "truncated")
            .GroupBy(selection => selection.Path, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.Ordinal) ?? [];
        var effectiveProfileBudget = manifest.ModelContextWindowTokens == 0
            ? 0
            : Math.Max(
                0,
                Math.Min(
                    manifest.ModelContextWindowTokens,
                    evidence.TaskContract.Budget.MaxTokens) -
                manifest.ReservedOutputTokens -
                manifest.PromptOverheadTokens);
        var invalid = manifest.StrategyVersion != ContextManifestSchema.StrategyVersion ||
            !string.Equals(manifest.TaskId, evidence.TaskContract.Id, StringComparison.Ordinal) ||
            !string.Equals(manifest.BaselineCommit, evidence.Baseline.Commit, StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(manifest.Source) ||
            string.IsNullOrWhiteSpace(manifest.Strategy) ||
            string.IsNullOrWhiteSpace(manifest.Tokenizer) ||
            string.IsNullOrWhiteSpace(manifest.TokenizerVersion) ||
            manifest.ModelContextWindowTokens < 0 ||
            manifest.ReservedOutputTokens < 0 ||
            manifest.PromptOverheadTokens < 0 ||
            manifest.DependencyDepth < 0 ||
            manifest.MaxFileTokens < 0 ||
            manifest.MaxTokens < 0 ||
            manifest.MaxCharacters < 0 ||
            manifest.EstimatedTokens < 0 ||
            manifest.TotalCharacters < 0 ||
            manifest.EligibleFileCount < 0 ||
            manifest.OmittedFileCount < 0 ||
            manifest.EstimatedTokens > manifest.MaxTokens ||
            manifest.TotalCharacters > manifest.MaxCharacters ||
            manifest.MaxTokens > effectiveProfileBudget ||
            manifest.Files is null ||
            manifest.Selections is null ||
            manifest.EligibleFileCount != manifest.Selections.Count ||
            manifest.OmittedFileCount != manifest.Selections.Count(selection =>
                selection.Decision == "omitted") ||
            manifest.Files.Count != includedSelections.Count ||
            manifest.Truncated != manifest.Selections.Any(selection =>
                selection.Decision != "included") ||
            manifest.Selections.Select(selection => selection.Path)
                .Distinct(StringComparer.Ordinal).Count() != manifest.Selections.Count ||
            manifest.Files.Select(file => file.Path)
                .Distinct(StringComparer.Ordinal).Count() != manifest.Files.Count ||
            manifest.Selections.Select(selection => selection.Rank)
                .OrderBy(rank => rank).SequenceEqual(
                    Enumerable.Range(1, manifest.Selections.Count)) == false ||
            manifest.Selections.Any(selection =>
                !IsSafeRepositoryPath(selection.Path) ||
                selection.Decision is not ("included" or "truncated" or "omitted") ||
                string.IsNullOrWhiteSpace(selection.Reason) ||
                selection.Rank <= 0 ||
                selection.Score < 0 ||
                selection.Depth < -1 ||
                selection.RankingReasons is null ||
                selection.OriginalTokens < 0 ||
                selection.IncludedTokens < 0 ||
                (!string.IsNullOrEmpty(selection.OriginalSha256) &&
                    !IsSha256(selection.OriginalSha256)) ||
                (!string.IsNullOrEmpty(selection.IncludedSha256) &&
                    !IsSha256(selection.IncludedSha256)) ||
                selection.Decision != "omitted" &&
                    (!IsSha256(selection.OriginalSha256) ||
                     !IsSha256(selection.IncludedSha256)) ||
                selection.Decision == "omitted" &&
                    (!string.IsNullOrEmpty(selection.IncludedSha256) ||
                     selection.IncludedTokens != 0)) ||
            manifest.Files.Any(file =>
                !IsSafeRepositoryPath(file.Path) ||
                !IsSha256(file.Sha256) ||
                !IsSha256(file.IncludedSha256) ||
                file.OriginalCharacters < 0 ||
                file.IncludedCharacters < 0 ||
                file.OriginalTokens < 0 ||
                file.IncludedTokens < 0 ||
                file.Rank <= 0 ||
                file.Score < 0 ||
                file.Depth < -1 ||
                file.Reasons is null ||
                file.Symbols is null ||
                !includedSelections.TryGetValue(file.Path, out var selection) ||
                selection.Rank != file.Rank ||
                selection.Score != file.Score ||
                selection.Depth != file.Depth ||
                selection.Relation != file.Relation ||
                !selection.RankingReasons.SequenceEqual(file.Reasons, StringComparer.Ordinal) ||
                selection.OriginalSha256 != file.Sha256 ||
                selection.IncludedSha256 != file.IncludedSha256 ||
                selection.OriginalTokens != file.OriginalTokens ||
                selection.IncludedTokens != file.IncludedTokens ||
                (selection.Decision == "truncated") != file.Truncated) ||
            !IsSha256(manifest.ManifestHash) ||
            !FixedTimeTextEquals(
                manifest.ManifestHash,
                ContextManifestFingerprint.Create(manifest)) ||
            manifest.Id != $"CTX-{manifest.ManifestHash[7..19]}" ||
            (!string.IsNullOrEmpty(manifest.RepositorySnapshotHash) &&
                (snapshot is null || !FixedTimeTextEquals(
                    manifest.RepositorySnapshotHash,
                    snapshot.SnapshotHash))) ||
            (!string.IsNullOrEmpty(manifest.SymbolGraphHash) &&
                (symbolGraph is null || !FixedTimeTextEquals(
                    manifest.SymbolGraphHash,
                    symbolGraph.GraphHash))) ||
            (!string.IsNullOrEmpty(manifest.SymbolGraphHash) &&
                string.IsNullOrEmpty(manifest.RepositorySnapshotHash));

        if (invalid)
        {
            throw new EvidenceIntegrityException(
                "Context manifest is incomplete, inconsistent, or has an invalid fingerprint.");
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
