using System.Security.Cryptography;
using System.Text;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Repositories;

internal static class EvidenceGraphProjection
{
    public static EvidenceGraph Project(
        SignedExecutionEvidenceEnvelope envelope,
        EvidenceReadScope scope)
    {
        var evidence = envelope.Evidence;
        var repositoryPath = NormalizePath(evidence.Baseline.RepositoryPath);
        var repositoryId = $"repository:{Hash(StablePath(repositoryPath))}";
        var builder = new Builder(
            evidence.Id,
            repositoryId,
            repositoryPath,
            scope.Principal,
            envelope.Seal.KeyId);

        var repositoryNode = repositoryId;
        var taskNode = $"task:{Hash($"{repositoryId}|{evidence.TaskContract.Id}")}";
        var executionNode = $"execution:{evidence.Id:N}";
        var runNode = $"run:{evidence.AgentRun.Id:N}";
        var baselineNode = $"baseline:{Hash($"{repositoryId}|{evidence.Baseline.Commit}")}";
        var contextNode = $"context:{evidence.Id:N}:{StableSegment(evidence.ContextManifest.Id)}";
        var candidateNode = $"candidate:{evidence.CandidateChangeSet.Id:N}";
        var decisionNode = $"decision:{evidence.Id:N}";

        builder.Node(repositoryNode, EvidenceGraphNodeKind.Repository, repositoryPath,
            "read-scope", scope.Principal, null,
            attributes: new() { ["path"] = repositoryPath });
        builder.Node(taskNode, EvidenceGraphNodeKind.Task, evidence.TaskContract.Id,
            "task-contract", envelope.Seal.KeyId, evidence.TaskContract.CreatedAt,
            attributes: new()
            {
                ["objective"] = evidence.TaskContract.Objective,
                ["status"] = evidence.TaskContract.Status.ToString()
            });
        builder.Node(executionNode, EvidenceGraphNodeKind.Execution, evidence.Id.ToString("N"),
            "execution-evidence", envelope.Seal.KeyId, evidence.CreatedAt,
            hashes: new() { ["payloadSha256"] = envelope.Seal.PayloadSha256 },
            attributes: new()
            {
                ["schemaVersion"] = envelope.SchemaVersion,
                ["signatureAlgorithm"] = envelope.Seal.Algorithm
            });
        builder.Edge(repositoryNode, taskNode, "contains", "read-scope", scope.Principal);
        builder.Edge(taskNode, executionNode, "has-execution", "execution-evidence",
            envelope.Seal.KeyId, evidence.CreatedAt);

        builder.Node(runNode, EvidenceGraphNodeKind.AgentRun,
            $"{evidence.AgentRun.Provider}/{evidence.AgentRun.Model}",
            "agent-run", envelope.Seal.KeyId, evidence.AgentRun.StartedAt,
            attributes: new()
            {
                ["agentType"] = evidence.AgentRun.AgentType,
                ["provider"] = evidence.AgentRun.Provider,
                ["model"] = evidence.AgentRun.Model,
                ["exitReason"] = evidence.AgentRun.ExitReason
            });
        if (string.Equals(
                evidence.AgentRun.TaskId,
                evidence.TaskContract.Id,
                StringComparison.Ordinal))
        {
            builder.Edge(taskNode, runNode, "executes", "agent-run", envelope.Seal.KeyId,
                evidence.AgentRun.StartedAt);
            builder.Edge(executionNode, runNode, "records", "execution-evidence",
                envelope.Seal.KeyId, evidence.AgentRun.StartedAt);
        }
        else
        {
            builder.Diagnostic("AgentRun task reference is invalid; task-to-run edge omitted.");
        }

        foreach (var attempt in evidence.AgentAttempts.OrderBy(item => item.AttemptNumber))
        {
            var attemptNode = $"attempt:{attempt.Id:N}";
            builder.Node(attemptNode, EvidenceGraphNodeKind.AgentAttempt,
                $"attempt {attempt.AttemptNumber}", "agent-attempt", envelope.Seal.KeyId,
                attempt.StartedAt,
                attributes: new()
                {
                    ["number"] = attempt.AttemptNumber.ToString(),
                    ["success"] = attempt.Success.ToString(),
                    ["failureKind"] = attempt.FailureKind.ToString(),
                    ["decisionReason"] = attempt.DecisionReason
                });
            builder.Edge(runNode, attemptNode, "has-attempt", "agent-attempt",
                envelope.Seal.KeyId, attempt.StartedAt);
        }

        builder.Node(baselineNode, EvidenceGraphNodeKind.Baseline,
            evidence.Baseline.Commit, "git-baseline", envelope.Seal.KeyId,
            evidence.Baseline.CapturedAt,
            hashes: new() { ["gitCommit"] = evidence.Baseline.Commit },
            attributes: new()
            {
                ["branch"] = evidence.Baseline.Branch,
                ["repositoryPath"] = repositoryPath
            });
        builder.Edge(executionNode, baselineNode, "uses-baseline", "execution-evidence",
            envelope.Seal.KeyId, evidence.Baseline.CapturedAt);

        var contextStatus = string.IsNullOrWhiteSpace(evidence.ContextManifest.Id)
            ? EvidenceGraphDataStatus.Missing
            : EvidenceGraphDataStatus.Valid;
        builder.Node(contextNode, EvidenceGraphNodeKind.Context,
            string.IsNullOrWhiteSpace(evidence.ContextManifest.Id)
                ? "missing context manifest"
                : evidence.ContextManifest.Id,
            evidence.ContextManifest.Source, envelope.Seal.KeyId, evidence.CreatedAt,
            contextStatus,
            hashes: new() { ["manifestSha256"] = evidence.ContextManifest.ManifestHash },
            attributes: new()
            {
                ["strategy"] = evidence.ContextManifest.Strategy,
                ["files"] = evidence.ContextManifest.Files.Count.ToString(),
                ["estimatedTokens"] = evidence.ContextManifest.EstimatedTokens.ToString()
            });
        if (string.IsNullOrWhiteSpace(evidence.ContextManifest.Id))
            builder.Diagnostic("Context manifest is missing.");
        var contextReferencesValid = string.Equals(
                evidence.ContextManifest.TaskId,
                evidence.TaskContract.Id,
                StringComparison.Ordinal) &&
            string.Equals(
                evidence.ContextManifest.BaselineCommit,
                evidence.Baseline.Commit,
                StringComparison.Ordinal);
        if (contextReferencesValid)
        {
            builder.Edge(baselineNode, contextNode, "compiles-context", "context-manifest",
                envelope.Seal.KeyId);
        }
        else if (!string.IsNullOrWhiteSpace(evidence.ContextManifest.Id))
        {
            builder.Diagnostic(
                "Context task or baseline reference is invalid; context edge omitted.");
        }

        builder.Node(candidateNode, EvidenceGraphNodeKind.Candidate,
            evidence.CandidateChangeSet.Id.ToString("N"), "git-candidate",
            envelope.Seal.KeyId, evidence.CandidateChangeSet.CreatedAt,
            hashes: new() { ["diffSha256"] = evidence.CandidateChangeSet.DiffHash },
            attributes: new()
            {
                ["changedFiles"] = evidence.CandidateChangeSet.ChangedFiles.Count.ToString(),
                ["addedFiles"] = evidence.CandidateChangeSet.AddedFiles.Count.ToString(),
                ["modifiedFiles"] = evidence.CandidateChangeSet.ModifiedFiles.Count.ToString(),
                ["deletedFiles"] = evidence.CandidateChangeSet.DeletedFiles.Count.ToString()
            });
        if (string.Equals(evidence.CandidateChangeSet.TaskId, evidence.TaskContract.Id,
                StringComparison.Ordinal) &&
            string.Equals(evidence.CandidateChangeSet.AgentRunId,
                evidence.AgentRun.Id.ToString("N"), StringComparison.OrdinalIgnoreCase))
        {
            builder.Edge(runNode, candidateNode, "produces", "git-candidate",
                envelope.Seal.KeyId, evidence.CandidateChangeSet.CreatedAt);
        }
        else
        {
            builder.Diagnostic(
                "Candidate task or run reference is invalid; run-to-candidate edge omitted.");
        }
        if (string.Equals(evidence.CandidateChangeSet.BaselineCommit,
                evidence.Baseline.Commit, StringComparison.Ordinal))
        {
            builder.Edge(baselineNode, candidateNode, "base-of", "git-candidate",
                envelope.Seal.KeyId, evidence.CandidateChangeSet.CreatedAt);
        }
        else
        {
            builder.Diagnostic("Candidate baseline reference is invalid; baseline edge omitted.");
        }
        if (!string.IsNullOrWhiteSpace(evidence.ContextManifest.Id) &&
            contextReferencesValid)
        {
            builder.Edge(contextNode, candidateNode, "informs", "context-manifest",
                envelope.Seal.KeyId, evidence.CandidateChangeSet.CreatedAt);
        }

        var commandNodes = new Dictionary<Guid, string>();
        AddCommands(builder, evidence.BaselineCommands, baselineNode, "baseline",
            envelope.Seal.KeyId, commandNodes);
        AddCommands(builder, evidence.CandidateCommands, candidateNode, "candidate",
            envelope.Seal.KeyId, commandNodes);

        var verificationNodes = new Dictionary<Guid, string>();
        AddVerifications(builder, evidence.BaselineVerificationResults, baselineNode,
            "baseline", evidence.AgentRun.Id, envelope.Seal.KeyId, verificationNodes,
            commandNodes);
        AddVerifications(builder, evidence.VerificationResults, candidateNode,
            "candidate", evidence.AgentRun.Id, envelope.Seal.KeyId, verificationNodes,
            commandNodes);

        builder.Node(decisionNode, EvidenceGraphNodeKind.Decision,
            evidence.FinalDecision.Decision.ToString(), "decision-engine",
            envelope.Seal.KeyId, evidence.FinalDecision.DecidedAt,
            hashes: new() { ["evidencePayloadSha256"] = envelope.Seal.PayloadSha256 },
            attributes: new()
            {
                ["decision"] = evidence.FinalDecision.Decision.ToString(),
                ["state"] = evidence.FinalDecision.State.ToString(),
                ["reason"] = evidence.FinalDecision.Reason
            });
        builder.Edge(candidateNode, decisionNode, "evaluated-by", "decision-engine",
            envelope.Seal.KeyId, evidence.FinalDecision.DecidedAt);
        foreach (var verificationNode in verificationNodes.Values)
        {
            builder.Edge(verificationNode, decisionNode, "supports", "decision-engine",
                envelope.Seal.KeyId, evidence.FinalDecision.DecidedAt);
        }

        foreach (var (criterion, index) in evidence.AcceptanceCriteriaResults
                     .Select((item, index) => (item, index)))
        {
            var criterionSegment = string.IsNullOrWhiteSpace(criterion.CriterionId)
                ? $"missing-{index}"
                : StableSegment(criterion.CriterionId);
            var criterionNode = $"acceptance:{evidence.Id:N}:{criterionSegment}";
            var status = string.IsNullOrWhiteSpace(criterion.CriterionId)
                ? EvidenceGraphDataStatus.Missing
                : EvidenceGraphDataStatus.Valid;
            builder.Node(criterionNode, EvidenceGraphNodeKind.AcceptanceCriterion,
                string.IsNullOrWhiteSpace(criterion.CriterionId)
                    ? "missing criterion id"
                    : criterion.CriterionId,
                "acceptance-verifier", envelope.Seal.KeyId, evidence.FinalDecision.DecidedAt,
                status,
                attributes: new()
                {
                    ["description"] = criterion.Description,
                    ["required"] = criterion.Required.ToString(),
                    ["status"] = criterion.Status.ToString(),
                    ["evidenceType"] = criterion.EvidenceType.ToString(),
                    ["evidenceReference"] = criterion.EvidenceReference
                });
            builder.Edge(candidateNode, criterionNode, "requires", "task-contract",
                envelope.Seal.KeyId);
            builder.Edge(criterionNode, decisionNode, "supports", "acceptance-verifier",
                envelope.Seal.KeyId, evidence.FinalDecision.DecidedAt);

            foreach (var reference in criterion.EvidenceReferences)
            {
                if (!TryReferencedNode(
                        reference,
                        verificationNodes,
                        commandNodes,
                        out var sourceNode))
                {
                    if (reference.StartsWith("verification-result:",
                            StringComparison.OrdinalIgnoreCase) ||
                        reference.StartsWith("execution-command:",
                            StringComparison.OrdinalIgnoreCase))
                    {
                        builder.Diagnostic(
                            $"Acceptance criterion '{criterion.CriterionId}' has invalid or missing reference '{reference}'.");
                    }
                    continue;
                }

                builder.Edge(sourceNode, criterionNode, "is-evidence-for",
                    "acceptance-verifier", envelope.Seal.KeyId);
            }
        }

        var chainEvents = envelope.PromotionEvents
            .Select(item => new ChainProjection(
                item.Sequence,
                $"promotion:{item.Promotion.Id:N}",
                EvidenceGraphNodeKind.Promotion,
                item.Promotion.Action.ToString(),
                "promotion-service",
                item.Seal.KeyId,
                item.Seal.SignedAt,
                item.Seal.PayloadSha256,
                item.Promotion.Actor,
                item.Promotion.Status.ToString(),
                item.Promotion.DiffHash))
            .Concat(envelope.ReplayEvents.Select(item => new ChainProjection(
                item.Sequence,
                $"replay:{item.Replay.Id:N}",
                EvidenceGraphNodeKind.Replay,
                item.Replay.Outcome.ToString(),
                "replay-service",
                item.Seal.KeyId,
                item.Seal.SignedAt,
                item.Seal.PayloadSha256,
                string.Empty,
                item.Replay.Outcome.ToString(),
                item.Replay.ActualDiffHash)))
            .OrderBy(item => item.Sequence)
            .ToList();
        var previousEventNode = executionNode;
        foreach (var chainEvent in chainEvents)
        {
            builder.Node(chainEvent.NodeId, chainEvent.Kind, chainEvent.Label,
                chainEvent.Origin, chainEvent.Authority, chainEvent.Timestamp,
                hashes: new()
                {
                    ["payloadSha256"] = chainEvent.PayloadHash,
                    ["candidateDiffSha256"] = chainEvent.CandidateHash
                },
                attributes: new()
                {
                    ["sequence"] = chainEvent.Sequence.ToString(),
                    ["actor"] = chainEvent.Actor,
                    ["result"] = chainEvent.Result
                });
            builder.Edge(previousEventNode, chainEvent.NodeId, "authenticated-next",
                chainEvent.Origin, chainEvent.Authority, chainEvent.Timestamp);
            builder.Edge(
                chainEvent.Kind == EvidenceGraphNodeKind.Promotion
                    ? decisionNode
                    : candidateNode,
                chainEvent.NodeId,
                chainEvent.Kind == EvidenceGraphNodeKind.Promotion ? "authorizes" : "replayed-by",
                chainEvent.Origin,
                chainEvent.Authority,
                chainEvent.Timestamp);
            previousEventNode = chainEvent.NodeId;
        }

        return builder.Build(new EvidenceGraphSummary
        {
            EvidenceId = evidence.Id,
            RepositoryId = repositoryId,
            TaskId = evidence.TaskContract.Id,
            RunId = evidence.AgentRun.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            BaselineCommit = evidence.Baseline.Commit,
            Decision = evidence.FinalDecision.Decision,
            DiffHash = evidence.CandidateChangeSet.DiffHash,
            EvidenceHash = envelope.Seal.PayloadSha256,
            PromotionIds = envelope.PromotionEvents
                .Select(item => item.Promotion.Id).ToList(),
            ReplayIds = envelope.ReplayEvents.Select(item => item.Replay.Id).ToList(),
            CreatedAt = envelope.Seal.SignedAt,
            UpdatedAt = envelope.ChainSeal.SignedAt
        });
    }

    public static bool Matches(EvidenceGraphSummary summary, EvidenceGraphQuery query) =>
        (query.TaskId is null || string.Equals(summary.TaskId, query.TaskId,
            StringComparison.Ordinal)) &&
        (query.RunId is null || summary.RunId == query.RunId) &&
        (query.CandidateId is null || summary.CandidateId == query.CandidateId) &&
        (query.BaselineCommit is null || string.Equals(summary.BaselineCommit,
            query.BaselineCommit, StringComparison.Ordinal)) &&
        (query.Decision is null || summary.Decision == query.Decision) &&
        (query.PromotionId is null || summary.PromotionIds.Contains(query.PromotionId.Value));

    public static void EnsureAuthorized(
        string evidenceRepositoryPath,
        EvidenceReadScope scope)
    {
        if (string.IsNullOrWhiteSpace(scope.Principal))
            throw new UnauthorizedAccessException("Evidence read principal is required.");
        if (string.IsNullOrWhiteSpace(scope.RepositoryPath))
            throw new UnauthorizedAccessException("Evidence read repository scope is required.");

        var evidencePath = NormalizePath(evidenceRepositoryPath);
        var authorizedPath = NormalizePath(scope.RepositoryPath);
        if (!string.Equals(
                evidencePath,
                authorizedPath,
                OperatingSystem.IsWindows()
                    ? StringComparison.OrdinalIgnoreCase
                    : StringComparison.Ordinal))
        {
            throw new UnauthorizedAccessException(
                "Principal is not authorized to read evidence from another repository scope.");
        }
    }

    private static void AddCommands(
        Builder builder,
        IReadOnlyList<ExecutionCommandEvidence> commands,
        string ownerNode,
        string phase,
        string authority,
        Dictionary<Guid, string> commandNodes)
    {
        foreach (var command in commands)
        {
            var commandNode = $"command:{command.Id:N}";
            var attributes = new Dictionary<string, string>
            {
                ["phase"] = phase,
                ["fileName"] = command.FileName,
                ["arguments"] = string.Join(" ", command.Arguments),
                ["workingDirectory"] = command.WorkingDirectory,
                ["exitCode"] = command.ExitCode.ToString(),
                ["timedOut"] = command.TimedOut.ToString(),
                ["cancelled"] = command.Cancelled.ToString()
            };
            if (command.Environment is not null)
            {
                attributes["runtime"] = command.Environment.Runtime;
                attributes["runtimeVersion"] = command.Environment.RuntimeVersion;
                attributes["image"] = command.Environment.Image;
                attributes["imageDigest"] = command.Environment.ImageDigest;
                attributes["networkMode"] = command.Environment.NetworkMode;
                attributes["cpuLimit"] = command.Environment.CpuLimit;
                attributes["memoryLimit"] = command.Environment.MemoryLimit;
                attributes["processLimit"] = command.Environment.ProcessLimit.ToString();
                attributes["wallClockLimitSeconds"] =
                    command.Environment.WallClockLimitSeconds.ToString();
                attributes["workspaceMount"] = command.Environment.WorkspaceMount;
                attributes["developmentHostOverride"] =
                    command.Environment.DevelopmentHostOverride.ToString();
                if (command.Environment.Capabilities is not null)
                {
                    attributes["capabilityPolicyVersion"] =
                        command.Environment.Capabilities.PolicyVersion;
                    attributes["capabilityAuthority"] =
                        command.Environment.Capabilities.Authority;
                    attributes["capabilityPolicyHash"] =
                        command.Environment.Capabilities.PolicyHash;
                    attributes["capabilityPhase"] =
                        command.Environment.Capabilities.Phase;
                    attributes["capabilityGranted"] =
                        string.Join(";", command.Environment.Capabilities.Granted);
                    attributes["capabilityDenied"] =
                        string.Join(";", command.Environment.Capabilities.Denied);
                    attributes["capabilityInjectedSecrets"] =
                        string.Join(";", command.Environment.Capabilities.InjectedSecrets);
                }
            }

            builder.Node(commandNode, EvidenceGraphNodeKind.Command,
                $"{command.FileName} {string.Join(' ', command.Arguments)}".Trim(),
                $"{phase}-command", authority, null,
                attributes: attributes);
            commandNodes[command.Id] = commandNode;
            builder.Edge(ownerNode, commandNode, "executes-command", $"{phase}-command",
                authority);
        }
    }

    private static void AddVerifications(
        Builder builder,
        IReadOnlyList<VerificationResult> verifications,
        string ownerNode,
        string phase,
        Guid runId,
        string authority,
        Dictionary<Guid, string> verificationNodes,
        IReadOnlyDictionary<Guid, string> commandNodes)
    {
        foreach (var verification in verifications)
        {
            var verificationNode = $"verification:{verification.Id:N}";
            var attributes = new Dictionary<string, string>
            {
                ["phase"] = phase,
                ["verifier"] = verification.Verifier,
                ["status"] = verification.Status.ToString(),
                ["severity"] = verification.Severity.ToString(),
                ["message"] = verification.Message
            };
            if (verification.SecurityScan is not null)
            {
                attributes["securityScanSchema"] = verification.SecurityScan.SchemaVersion;
                attributes["securityPolicyHash"] = verification.SecurityScan.PolicyHash;
                attributes["vulnerabilityDatabase"] =
                    verification.SecurityScan.VulnerabilityDatabaseVersion;
                attributes["securityFindings"] = string.Join(";", verification.SecurityScan.Findings
                    .Select(finding =>
                        $"{finding.Rule}@{finding.Path}:{finding.Line}:{finding.Severity}:{finding.Disposition}"));
            }
            if (verification.TestSuite is not null)
            {
                attributes["testSuiteSchema"] = verification.TestSuite.SchemaVersion;
                attributes["testSuiteCategory"] = verification.TestSuite.Category.ToString();
                attributes["testSuiteMode"] = verification.TestSuite.Mode.ToString();
                attributes["testSuiteTarget"] = verification.TestSuite.Target;
                attributes["testsDiscovered"] = verification.TestSuite.Discovered.ToString();
                attributes["testsExecuted"] = verification.TestSuite.Executed.ToString();
                attributes["testsPassed"] = verification.TestSuite.Passed.ToString();
                attributes["testsFailed"] = verification.TestSuite.Failed.ToString();
                attributes["testsSkipped"] = verification.TestSuite.Skipped.ToString();
                attributes["testCommandEvidenceId"] =
                    verification.TestSuite.CommandEvidenceId.ToString("N");
            }
            builder.Node(verificationNode, EvidenceGraphNodeKind.Verification,
                verification.Verifier, $"{phase}-verification", authority,
                verification.CreatedAt,
                attributes: attributes);
            verificationNodes[verification.Id] = verificationNode;
            if (string.Equals(verification.AgentRunId, runId.ToString("N"),
                    StringComparison.OrdinalIgnoreCase))
            {
                builder.Edge(ownerNode, verificationNode, "verified-by",
                    $"{phase}-verification", authority, verification.CreatedAt);
            }
            else
            {
                builder.Diagnostic(
                    $"Verification '{verification.Id:N}' has an invalid run reference; edge omitted.");
            }
            if (verification.TestSuite?.CommandEvidenceId is { } commandId &&
                commandId != Guid.Empty)
            {
                if (commandNodes.TryGetValue(commandId, out var commandNode))
                {
                    builder.Edge(commandNode, verificationNode,
                        "produces-test-suite-evidence", $"{phase}-verification",
                        authority, verification.CreatedAt);
                }
                else
                {
                    builder.Diagnostic(
                        $"Verification '{verification.Id:N}' has an invalid test command reference; edge omitted.");
                }
            }
        }
    }

    private static bool TryReferencedNode(
        string reference,
        IReadOnlyDictionary<Guid, string> verificationNodes,
        IReadOnlyDictionary<Guid, string> commandNodes,
        out string nodeId)
    {
        const string verificationPrefix = "verification-result:";
        const string commandPrefix = "execution-command:";
        nodeId = string.Empty;
        if (reference.StartsWith(verificationPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(reference[verificationPrefix.Length..], out var verificationId))
        {
            return verificationNodes.TryGetValue(verificationId, out nodeId!);
        }

        return reference.StartsWith(commandPrefix, StringComparison.OrdinalIgnoreCase) &&
            Guid.TryParse(reference[commandPrefix.Length..], out var commandId) &&
            commandNodes.TryGetValue(commandId, out nodeId!);
    }

    private static string NormalizePath(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static string StablePath(string path) =>
        OperatingSystem.IsWindows() ? path.ToUpperInvariant() : path;

    private static string StableSegment(string value) => string.IsNullOrWhiteSpace(value)
        ? "missing"
        : Uri.EscapeDataString(value.Trim());

    private static string Hash(string value)
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
        return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
    }

    private sealed record ChainProjection(
        int Sequence,
        string NodeId,
        EvidenceGraphNodeKind Kind,
        string Label,
        string Origin,
        string Authority,
        DateTime Timestamp,
        string PayloadHash,
        string Actor,
        string Result,
        string CandidateHash);

    private sealed class Builder
    {
        private readonly Guid _evidenceId;
        private readonly string _repositoryId;
        private readonly string _repositoryPath;
        private readonly string _principal;
        private readonly string _defaultAuthority;
        private readonly Dictionary<string, EvidenceGraphNode> _nodes =
            new(StringComparer.Ordinal);
        private readonly Dictionary<string, EvidenceGraphEdge> _edges =
            new(StringComparer.Ordinal);
        private readonly List<string> _diagnostics = [];

        public Builder(
            Guid evidenceId,
            string repositoryId,
            string repositoryPath,
            string principal,
            string defaultAuthority)
        {
            _evidenceId = evidenceId;
            _repositoryId = repositoryId;
            _repositoryPath = repositoryPath;
            _principal = principal;
            _defaultAuthority = defaultAuthority;
        }

        public void Node(
            string id,
            EvidenceGraphNodeKind kind,
            string label,
            string origin,
            string authority,
            DateTime? timestamp,
            EvidenceGraphDataStatus status = EvidenceGraphDataStatus.Valid,
            Dictionary<string, string>? hashes = null,
            Dictionary<string, string>? attributes = null)
        {
            if (_nodes.ContainsKey(id))
            {
                Diagnostic($"Duplicate stable node ID '{id}' was rejected.");
                return;
            }

            _nodes[id] = new EvidenceGraphNode
            {
                Id = id,
                Kind = kind,
                Label = label,
                Origin = origin,
                Authority = string.IsNullOrWhiteSpace(authority)
                    ? _defaultAuthority
                    : authority,
                Timestamp = timestamp,
                Status = status,
                Hashes = hashes ?? [],
                Attributes = attributes ?? []
            };
        }

        public void Edge(
            string from,
            string to,
            string kind,
            string origin,
            string authority,
            DateTime? timestamp = null)
        {
            if (!_nodes.ContainsKey(from) || !_nodes.ContainsKey(to))
            {
                Diagnostic($"Edge '{kind}' references a missing node and was omitted.");
                return;
            }

            var id = $"edge:{Hash($"{from}|{kind}|{to}")}";
            if (_edges.ContainsKey(id))
                return;
            _edges[id] = new EvidenceGraphEdge
            {
                Id = id,
                From = from,
                To = to,
                Kind = kind,
                Origin = origin,
                Authority = string.IsNullOrWhiteSpace(authority)
                    ? _defaultAuthority
                    : authority,
                Timestamp = timestamp
            };
        }

        public void Diagnostic(string message)
        {
            if (!_diagnostics.Contains(message, StringComparer.Ordinal))
                _diagnostics.Add(message);
        }

        public EvidenceGraph Build(EvidenceGraphSummary summary) => new()
        {
            Id = $"graph:{_evidenceId:N}",
            EvidenceId = _evidenceId,
            RepositoryId = _repositoryId,
            RepositoryPath = _repositoryPath,
            Principal = _principal,
            Summary = summary,
            Nodes = _nodes.Values.OrderBy(node => node.Id, StringComparer.Ordinal).ToList(),
            Edges = _edges.Values.OrderBy(edge => edge.Id, StringComparer.Ordinal).ToList(),
            Diagnostics = _diagnostics.OrderBy(item => item, StringComparer.Ordinal).ToList()
        };
    }
}
