using System.Security.Cryptography;
using System.Text;
using AECS.Application.EvidenceGraph;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class EvidenceGraphTests
{
    [Fact]
    public async Task AuthenticatedAggregate_ProjectsStableCausalGraphAndAllQueryFilters()
    {
        await using var fixture = EvidenceGraphFixture.Create();
        var evidence = fixture.CreateEvidence(fixture.RepositoryOne, "TASK-GRAPH-ONE");
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var promotion = fixture.CreatePromotion(evidence);
        var replay = fixture.CreateReplay(evidence);
        await fixture.Store.AppendPromotionAsync(evidence.Id, promotion, CancellationToken.None);
        await fixture.Store.AppendReplayAsync(evidence.Id, replay, CancellationToken.None);
        var service = new EvidenceGraphService(fixture.Store);
        var scope = fixture.Scope(fixture.RepositoryOne);

        var first = await service.ShowAsync(evidence.Id, scope, CancellationToken.None);
        var second = await service.TraceAsync(evidence.Id, scope, CancellationToken.None);

        first.Should().NotBeNull();
        second.Should().NotBeNull();
        first!.Nodes.Select(item => item.Id).Should().Equal(
            second!.Nodes.Select(item => item.Id));
        first.Edges.Select(item => item.Id).Should().Equal(
            second.Edges.Select(item => item.Id));
        first.Nodes.Select(item => item.Kind).Should().Contain(
        [
            EvidenceGraphNodeKind.Repository,
            EvidenceGraphNodeKind.Task,
            EvidenceGraphNodeKind.Execution,
            EvidenceGraphNodeKind.AgentRun,
            EvidenceGraphNodeKind.AgentAttempt,
            EvidenceGraphNodeKind.Baseline,
            EvidenceGraphNodeKind.RepositorySnapshot,
            EvidenceGraphNodeKind.CSharpSymbolGraph,
            EvidenceGraphNodeKind.Context,
            EvidenceGraphNodeKind.Candidate,
            EvidenceGraphNodeKind.Command,
            EvidenceGraphNodeKind.Verification,
            EvidenceGraphNodeKind.AcceptanceCriterion,
            EvidenceGraphNodeKind.Decision,
            EvidenceGraphNodeKind.Promotion,
            EvidenceGraphNodeKind.Replay
        ]);
        first.Nodes.Where(item => item.Kind != EvidenceGraphNodeKind.Repository)
            .Should().OnlyContain(item =>
                !string.IsNullOrWhiteSpace(item.Origin) &&
                !string.IsNullOrWhiteSpace(item.Authority));
        first.Nodes.Single(item => item.Kind == EvidenceGraphNodeKind.Decision)
            .Should().Match<EvidenceGraphNode>(item =>
                item.Timestamp.HasValue && item.Hashes.ContainsKey("evidencePayloadSha256"));
        first.Nodes.Single(item => item.Kind == EvidenceGraphNodeKind.Candidate)
            .Hashes["diffSha256"].Should().Be(evidence.CandidateChangeSet.DiffHash);
        first.Edges.Count(item => item.Kind == "authenticated-next").Should().Be(2);
        first.Edges.Should().Contain(item => item.Kind == "described-by");
        first.Edges.Should().Contain(item => item.Kind == "derives-symbol-graph");
        first.Edges.Should().Contain(item => item.Kind == "records-symbol-graph");
        first.Edges.Should().Contain(item => item.Kind == "informs-context");
        first.Nodes.Single(item => item.Kind == EvidenceGraphNodeKind.CSharpSymbolGraph)
            .Hashes["graphSha256"].Should().Be(evidence.CSharpSymbolGraph!.GraphHash);
        first.Diagnostics.Should().BeEmpty();

        var filters = new EvidenceGraphQuery[]
        {
            new() { TaskId = evidence.TaskContract.Id },
            new() { RunId = evidence.AgentRun.Id },
            new() { CandidateId = evidence.CandidateChangeSet.Id },
            new() { BaselineCommit = evidence.Baseline.Commit },
            new() { Decision = evidence.FinalDecision.Decision },
            new() { PromotionId = promotion.Id }
        };
        foreach (var filter in filters)
        {
            var result = await service.ListAsync(filter, scope, CancellationToken.None);
            result.Items.Should().ContainSingle().Which.EvidenceId.Should().Be(evidence.Id);
        }

        EvidenceGraphFormatter.ToJson(first).Should()
            .Contain("\"schemaVersion\": \"aecs.evidence-graph/v1\"")
            .And.Contain("\"authenticated-next\"");
        EvidenceGraphFormatter.ToDot(first).Should()
            .StartWith("digraph EvidenceGraph")
            .And.Contain("authenticated-next");
    }

    [Fact]
    public async Task RepositoryScope_BlocksCrossRepositoryReadsAndListDoesNotLeakThem()
    {
        await using var fixture = EvidenceGraphFixture.Create();
        var first = fixture.CreateEvidence(fixture.RepositoryOne, "TASK-ONE");
        var second = fixture.CreateEvidence(fixture.RepositoryTwo, "TASK-TWO");
        await fixture.Store.SaveAsync(first, CancellationToken.None);
        await fixture.Store.SaveAsync(second, CancellationToken.None);
        var service = new EvidenceGraphService(fixture.Store);
        var scope = fixture.Scope(fixture.RepositoryOne);

        var list = await service.ListAsync(new EvidenceGraphQuery(), scope, CancellationToken.None);
        list.Items.Should().ContainSingle().Which.EvidenceId.Should().Be(first.Id);
        var unauthorized = () => service.ShowAsync(
            second.Id,
            scope,
            CancellationToken.None);
        await unauthorized.Should().ThrowAsync<UnauthorizedAccessException>()
            .WithMessage("*another repository scope*");
    }

    [Fact]
    public async Task InvalidExplicitRelationship_IsReportedAndNeverInferred()
    {
        await using var fixture = EvidenceGraphFixture.Create();
        var evidence = fixture.CreateEvidence(
            fixture.RepositoryOne,
            "TASK-GRAPH-INVALID",
            invalidContextReference: true);
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);

        var graph = await new EvidenceGraphService(fixture.Store).ShowAsync(
            evidence.Id,
            fixture.Scope(fixture.RepositoryOne),
            CancellationToken.None);

        graph!.Diagnostics.Should().Contain(item =>
            item.Contains("Context task or baseline reference is invalid"));
        var context = graph.Nodes.Single(item => item.Kind == EvidenceGraphNodeKind.Context);
        graph.Edges.Should().NotContain(item =>
            item.To == context.Id && item.Kind == "compiles-context");
        graph.Edges.Should().NotContain(item =>
            item.From == context.Id && item.Kind == "informs");
    }

    [Fact]
    public async Task InvalidAuthenticatedRecord_IsOmittedWithGenericDiagnostic()
    {
        await using var fixture = EvidenceGraphFixture.Create();
        var valid = fixture.CreateEvidence(fixture.RepositoryOne, "TASK-VALID");
        var invalid = fixture.CreateEvidence(fixture.RepositoryOne, "TASK-TAMPERED");
        await fixture.Store.SaveAsync(valid, CancellationToken.None);
        var invalidPath = await fixture.Store.SaveAsync(invalid, CancellationToken.None);
        var json = await File.ReadAllTextAsync(invalidPath);
        await File.WriteAllTextAsync(
            invalidPath,
            json.Replace("TASK-TAMPERED", "TASK-FORGED", StringComparison.Ordinal));

        var result = await new EvidenceGraphService(fixture.Store).ListAsync(
            new EvidenceGraphQuery(),
            fixture.Scope(fixture.RepositoryOne),
            CancellationToken.None);

        result.Items.Should().ContainSingle().Which.EvidenceId.Should().Be(valid.Id);
        result.Diagnostics.Should().ContainSingle()
            .Which.Should().Be("An evidence record was invalid and was omitted.");
    }

    private sealed class EvidenceGraphFixture : IAsyncDisposable
    {
        private const string RootPrefix = "aecs-evidence-graph-tests-";

        private EvidenceGraphFixture(string rootPath)
        {
            RootPath = rootPath;
            RepositoryOne = Path.Combine(rootPath, "repository-one");
            RepositoryTwo = Path.Combine(rootPath, "repository-two");
            Directory.CreateDirectory(RepositoryOne);
            Directory.CreateDirectory(RepositoryTwo);
            Store = new JsonExecutionEvidenceStore(
                Path.Combine(rootPath, "evidence"),
                Path.Combine(rootPath, "keys"));
        }

        public string RootPath { get; }
        public string RepositoryOne { get; }
        public string RepositoryTwo { get; }
        public JsonExecutionEvidenceStore Store { get; }

        public static EvidenceGraphFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), $"{RootPrefix}{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new EvidenceGraphFixture(root);
        }

        public EvidenceReadScope Scope(string repositoryPath) => new()
        {
            RepositoryPath = repositoryPath,
            Principal = "integration-test"
        };

        public ExecutionEvidence CreateEvidence(
            string repositoryPath,
            string taskId,
            bool invalidContextReference = false)
        {
            var runId = Guid.NewGuid();
            var candidateId = Guid.NewGuid();
            var verificationId = Guid.NewGuid();
            var baseline = Convert.ToHexString(RandomNumberGenerator.GetBytes(20)).ToLowerInvariant();
            var diff = "diff --git a/src/file.cs b/src/file.cs\n-old\n+new\n";
            var now = DateTime.UtcNow;
            var repositorySnapshot = Snapshot(taskId, baseline);
            var symbolGraph = SymbolGraph(repositorySnapshot, baseline);
            return new ExecutionEvidence
            {
                TaskContract = new TaskContract
                {
                    Id = taskId,
                    Objective = "Project authenticated evidence graph"
                },
                AgentRun = new AgentRun
                {
                    Id = runId,
                    TaskId = taskId,
                    AgentType = "TestAgent",
                    Provider = "integration",
                    Model = "deterministic",
                    StartedAt = now,
                    FinishedAt = now.AddSeconds(1),
                    ExitReason = "Completed"
                },
                AgentResult = new AgentRunResult { Success = true },
                AgentAttempts =
                [
                    new AgentAttemptEvidence
                    {
                        AttemptNumber = 1,
                        StartedAt = now,
                        FinishedAt = now.AddMilliseconds(10),
                        Success = true,
                        DecisionReason = "passed"
                    }
                ],
                Baseline = new BaselineSnapshot
                {
                    Commit = baseline,
                    Branch = "dev",
                    RepositoryPath = repositoryPath,
                    CapturedAt = now
                },
                RepositorySnapshot = repositorySnapshot,
                CSharpSymbolGraph = symbolGraph,
                BaselineCommands =
                [
                    new ExecutionCommandEvidence
                    {
                        FileName = "dotnet",
                        Arguments = ["build", "Fixture.sln"],
                        WorkingDirectory = ".",
                        ExitCode = 0
                    }
                ],
                BaselineVerificationResults =
                [
                    Verification(runId, "Build", now)
                ],
                ContextManifest = new ContextManifest
                {
                    Id = $"CTX-{candidateId:N}",
                    TaskId = invalidContextReference ? "another-task" : taskId,
                    BaselineCommit = baseline,
                    ManifestHash = Hash("context"),
                    Source = "isolated-git-worktree",
                    Strategy = "scope+task-relevance+tests+roslyn-symbol-graph",
                    SemanticIndex = "roslyn-symbol-graph",
                    SymbolGraphHash = symbolGraph.GraphHash
                },
                CandidateChangeSet = new CandidateChangeSet
                {
                    Id = candidateId,
                    TaskId = taskId,
                    AgentRunId = runId.ToString("N"),
                    BaselineCommit = baseline,
                    ModifiedFiles = ["src/file.cs"],
                    Diff = diff,
                    DiffHash = Hash(diff),
                    CreatedAt = now.AddSeconds(1)
                },
                CandidateCommands =
                [
                    new ExecutionCommandEvidence
                    {
                        FileName = "dotnet",
                        Arguments = ["test", "Fixture.sln", "--no-build"],
                        WorkingDirectory = ".",
                        ExitCode = 0
                    }
                ],
                VerificationResults =
                [
                    new VerificationResult
                    {
                        Id = verificationId,
                        AgentRunId = runId.ToString("N"),
                        Verifier = "Tests",
                        Status = VerificationStatus.Pass,
                        Severity = Severity.Info,
                        Message = "passed",
                        CreatedAt = now.AddSeconds(2)
                    }
                ],
                AcceptanceCriteriaResults =
                [
                    new AcceptanceCriterionResult
                    {
                        CriterionId = "AC-001",
                        Description = "tests pass",
                        Required = true,
                        EvidenceType = AcceptanceEvidenceType.Verifier,
                        EvidenceReference = "Tests",
                        Status = VerificationStatus.Pass,
                        EvidenceReferences = [$"verification-result:{verificationId:N}"]
                    }
                ],
                FinalDecision = new FinalDecisionRecord
                {
                    Decision = TaskDecision.Verified,
                    State = TaskState.Verified,
                    Reason = "all required gates passed",
                    DecidedAt = now.AddSeconds(3)
                },
                CreatedAt = now
            };
        }

        public CandidatePromotionEvidence CreatePromotion(ExecutionEvidence evidence) => new()
        {
            ExecutionEvidenceId = evidence.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            Action = CandidatePromotionAction.ExportPatch,
            Status = CandidatePromotionStatus.Exported,
            Eligibility = PromotionEligibility.Verified,
            Actor = "operator",
            ApprovalKind = PromotionApprovalKind.Policy,
            ApprovalReference = "policy/graph-test",
            BaselineCommit = evidence.Baseline.Commit,
            DiffHash = evidence.CandidateChangeSet.DiffHash,
            RepositoryPath = evidence.Baseline.RepositoryPath,
            OutputPath = Path.Combine(RootPath, "candidate.patch")
        };

        public ExecutionReplayEvidence CreateReplay(ExecutionEvidence evidence) => new()
        {
            ExecutionEvidenceId = evidence.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            Outcome = ExecutionReplayOutcome.Reproduced,
            RepositoryPath = evidence.Baseline.RepositoryPath,
            RequestedRepositoryPath = evidence.Baseline.RepositoryPath,
            BaselineCommit = evidence.Baseline.Commit,
            ExpectedDiffHash = evidence.CandidateChangeSet.DiffHash,
            ActualDiffHash = evidence.CandidateChangeSet.DiffHash,
            ExpectedRepositorySnapshotHash = evidence.RepositorySnapshot?.SnapshotHash,
            ActualRepositorySnapshotHash = evidence.RepositorySnapshot?.SnapshotHash,
            ExpectedCSharpSymbolGraphHash = evidence.CSharpSymbolGraph?.GraphHash,
            ActualCSharpSymbolGraphHash = evidence.CSharpSymbolGraph?.GraphHash,
            RepositorySnapshotDiff = evidence.RepositorySnapshot is null
                ? null
                : new RepositorySnapshotDiff
                {
                    FromSnapshotHash = evidence.RepositorySnapshot.SnapshotHash,
                    ToSnapshotHash = evidence.RepositorySnapshot.SnapshotHash
                },
            StartedAt = DateTime.UtcNow.AddSeconds(-1),
            FinishedAt = DateTime.UtcNow
        };

        public ValueTask DisposeAsync()
        {
            var root = Path.GetFullPath(RootPath);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!root.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith(RootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to delete unexpected path: {root}");
            }

            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }

        private static VerificationResult Verification(Guid runId, string verifier, DateTime at) =>
            new()
            {
                AgentRunId = runId.ToString("N"),
                Verifier = verifier,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = "passed",
                CreatedAt = at
            };

        private static RepositorySnapshot Snapshot(string taskId, string baseline)
        {
            var configurationHash = RepositorySnapshotFingerprint.CreateConfiguration(
                RepositorySnapshotSchema.ProfileVersion,
                []);
            var draft = new RepositorySnapshot
            {
                ConfigurationHash = configurationHash,
                BaselineCommit = baseline,
                TaskContractId = taskId
            };
            return new RepositorySnapshot
            {
                SnapshotHash = RepositorySnapshotFingerprint.Create(draft),
                ConfigurationHash = configurationHash,
                BaselineCommit = baseline,
                TaskContractId = taskId
            };
        }

        private static CSharpSymbolGraph SymbolGraph(
            RepositorySnapshot snapshot,
            string baseline)
        {
            var draft = new CSharpSymbolGraph
            {
                RepositorySnapshotHash = snapshot.SnapshotHash,
                BaselineCommit = baseline,
                CompilerVersion = "5.0.0",
                MsBuildVersion = "17.14.0",
                SdkVersion = "9.0.100",
                LoadSucceeded = true
            };
            return new CSharpSymbolGraph
            {
                GraphHash = CSharpSymbolGraphFingerprint.Create(draft),
                RepositorySnapshotHash = draft.RepositorySnapshotHash,
                BaselineCommit = draft.BaselineCommit,
                CompilerVersion = draft.CompilerVersion,
                MsBuildVersion = draft.MsBuildVersion,
                SdkVersion = draft.SdkVersion,
                LoadSucceeded = draft.LoadSucceeded
            };
        }

        private static string Hash(string value)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
        }
    }
}
