using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using AECS.Application.ContextCompiler;
using AECS.Application.Jarvis;
using AECS.Cli.Jarvis;
using AECS.Cli.Runtime;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

[Collection(DurableJarvisConsoleCollection.Name)]
public sealed class DurableExecutionHistoryTests
{
    [Fact]
    public async Task RestartedJarvisCommands_UseDurableStoreWithHumanAndJsonOutput()
    {
        await using var fixture = Fixture.Create();
        var evidence = fixture.CreateEvidence(fixture.RepositoryPath);
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var originalInput = Console.In;
        var originalOutput = Console.Out;
        await using var output = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(string.Join(Environment.NewLine,
                "status --json",
                $"history --run {evidence.AgentRun.Id:N}",
                $"explain --evidence {evidence.Id:N} --json",
                $"context {evidence.TaskContract.Id}",
                "exit")));
            Console.SetOut(output);
            using var runtime = AecsExecutionRuntime.CreateForTesting(
                new MockAgentAdapter(),
                fixture.RestartStore());
            var exitCode = await new JarvisRepl(
                fixture.RepositoryPath,
                runtime).RunAsync(CancellationToken.None);
            exitCode.Should().Be(0);
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
        }

        var text = output.ToString();
        text.Should().Contain("aecs.jarvis-history/v1")
            .And.Contain("aecs.jarvis-explanation-result/v1")
            .And.Contain("[persisted] evidence=")
            .And.Contain("[persisted] context status=Complete")
            .And.Contain(evidence.AgentRun.Id.ToString("N"))
            .And.Contain(evidence.CandidateChangeSet.DiffHash);
    }

    [Fact]
    public async Task JarvisExecutionError_IsReportedThroughProcessExitCode()
    {
        await using var fixture = Fixture.Create();
        using var runtime = AecsExecutionRuntime.CreateForTesting(
            new MockAgentAdapter(),
            fixture.RestartStore());
        var originalInput = Console.In;
        var originalOutput = Console.Out;
        await using var output = new StringWriter();
        try
        {
            Console.SetIn(new StringReader(string.Join(Environment.NewLine,
                "run missing-task.yaml",
                "exit")));
            Console.SetOut(output);

            var exitCode = await new JarvisRepl(
                fixture.RepositoryPath,
                runtime).RunAsync(CancellationToken.None);

            exitCode.Should().Be(1);
            output.ToString().Should().Contain("Error:");
        }
        finally
        {
            Console.SetIn(originalInput);
            Console.SetOut(originalOutput);
        }
    }

    [Fact]
    public async Task RestartedService_QueriesAndExplainsAuthenticatedExecutionByEveryIdentity()
    {
        await using var fixture = Fixture.Create();
        var evidence = fixture.CreateEvidence(fixture.RepositoryPath);
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        await fixture.Store.AppendPromotionAsync(
            evidence.Id,
            fixture.CreatePromotion(evidence),
            CancellationToken.None);
        var restarted = fixture.RestartStore();
        var service = new DurableExecutionHistoryService(
            restarted,
            restarted,
            fixture.RepositoryPath,
            "integration-test");

        var status = await service.StatusAsync(CancellationToken.None);
        var byTask = await service.QueryAsync(
            new DurableHistoryQuery { TaskId = evidence.TaskContract.Id },
            CancellationToken.None);
        var byRun = await service.QueryAsync(
            new DurableHistoryQuery { RunId = evidence.AgentRun.Id },
            CancellationToken.None);
        var byCandidate = await service.QueryAsync(
            new DurableHistoryQuery { CandidateId = evidence.CandidateChangeSet.Id },
            CancellationToken.None);
        var explanation = await service.ExplainAsync(
            new DurableHistoryQuery { EvidenceId = evidence.Id },
            CancellationToken.None);
        var context = await service.ContextAsync(
            new DurableHistoryQuery { EvidenceId = evidence.Id },
            CancellationToken.None);

        status.Items.Should().ContainSingle();
        byTask.Items.Should().ContainSingle();
        byRun.Items.Should().ContainSingle();
        byCandidate.Items.Should().ContainSingle();
        var summary = status.Items[0];
        summary.Status.Should().Be(JarvisEvidenceStatus.Complete);
        summary.Authority.Should().StartWith("sha256:");
        summary.EvidenceHash.Should().StartWith("sha256:");
        summary.RunId.Should().Be(evidence.AgentRun.Id);
        summary.CandidateId.Should().Be(evidence.CandidateChangeSet.Id);

        explanation.Item.Should().NotBeNull();
        explanation.Item!.Persisted.Origin.Should().Be(JarvisFactKind.Persisted);
        explanation.Item.Persisted.Gates.Should().ContainSingle(gate =>
            gate.Verifier == "Build" && gate.Status == VerificationStatus.Pass);
        explanation.Item.Persisted.AcceptanceCriteria.Should().ContainSingle();
        explanation.Item.Persisted.Attempts.Should().ContainSingle();
        explanation.Item.Persisted.Promotions.Should().ContainSingle(promotion =>
            promotion.Status == CandidatePromotionStatus.Exported.ToString());
        explanation.Item.Derived.Origin.Should().Be(JarvisFactKind.Derived);
        explanation.Item.Derived.TotalTokens.Should().Be(150);
        explanation.Item.Interpretation.Origin.Should().Be(JarvisFactKind.Interpretation);
        explanation.Item.Interpretation.Summary.Should().Contain("Persisted decision Verified");
        context.Context.Should().NotBeNull();
        context.Context!.ManifestHash.Should().StartWith("sha256:");

        DurableExecutionHistoryFormatter.ToJson(explanation)
            .Should().Contain("\"schemaVersion\": \"aecs.jarvis-explanation-result/v1\"")
            .And.Contain("\"origin\": \"Persisted\"")
            .And.Contain(evidence.CandidateChangeSet.DiffHash);
        DurableExecutionHistoryFormatter.ExplanationToText(explanation)
            .Should().Contain("[persisted]")
            .And.Contain("[derived]")
            .And.Contain("[interpretation]")
            .And.Contain(summary.Authority);
    }

    [Fact]
    public async Task IncompleteAuthenticatedContext_IsReportedPartialWithoutInventingFacts()
    {
        await using var fixture = Fixture.Create();
        var evidence = fixture.CreateEvidence(
            fixture.RepositoryPath,
            context: new ContextManifest());
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var restarted = fixture.RestartStore();
        var service = new DurableExecutionHistoryService(
            restarted,
            restarted,
            fixture.RepositoryPath,
            "integration-test");

        var result = await service.ContextAsync(
            new DurableHistoryQuery { EvidenceId = evidence.Id },
            CancellationToken.None);

        result.Execution.Should().NotBeNull();
        result.Execution!.Status.Should().Be(JarvisEvidenceStatus.Partial);
        result.Context.Should().NotBeNull();
        result.Context!.Status.Should().Be(JarvisEvidenceStatus.Partial);
        result.Context.Id.Should().BeEmpty();
        result.Context.Files.Should().BeEmpty();
        result.Diagnostics.Should().Contain(message => message.Contains("context manifest"));
        DurableExecutionHistoryFormatter.ContextToText(result)
            .Should().Contain("status=Partial")
            .And.NotContain("Compiled Prompt Preview");
    }

    [Fact]
    public async Task RepositoryScope_OmitsOtherHistoryAndRejectsDirectCrossRepositoryRead()
    {
        await using var fixture = Fixture.Create();
        var otherRepository = Path.Combine(fixture.RootPath, "other-repository");
        Directory.CreateDirectory(otherRepository);
        var evidence = fixture.CreateEvidence(fixture.RepositoryPath);
        await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var restarted = fixture.RestartStore();
        var service = new DurableExecutionHistoryService(
            restarted,
            restarted,
            otherRepository,
            "integration-test");

        var history = await service.QueryAsync(
            new DurableHistoryQuery(),
            CancellationToken.None);
        var directRead = () => service.ExplainAsync(
            new DurableHistoryQuery { EvidenceId = evidence.Id },
            CancellationToken.None);

        history.Items.Should().BeEmpty();
        await directRead.Should().ThrowAsync<UnauthorizedAccessException>();
    }

    [Fact]
    public async Task TamperedHistoryAndDirectExplanation_AreOmittedAndDiagnosed()
    {
        await using var fixture = Fixture.Create();
        var evidence = fixture.CreateEvidence(fixture.RepositoryPath);
        var path = await fixture.Store.SaveAsync(evidence, CancellationToken.None);
        var document = JsonNode.Parse(await File.ReadAllTextAsync(path))!.AsObject();
        document["evidence"]!["finalDecision"]!["reason"] = "forged";
        await File.WriteAllTextAsync(path, document.ToJsonString());
        var restarted = fixture.RestartStore();
        var service = new DurableExecutionHistoryService(
            restarted,
            restarted,
            fixture.RepositoryPath,
            "integration-test");

        var result = await service.QueryAsync(
            new DurableHistoryQuery(),
            CancellationToken.None);
        var explanation = await service.ExplainAsync(
            new DurableHistoryQuery { EvidenceId = evidence.Id },
            CancellationToken.None);

        result.Items.Should().BeEmpty();
        result.Diagnostics.Should().Contain(message => message.Contains("invalid"));
        explanation.Item.Should().BeNull();
        explanation.Diagnostics.Should().Contain(message => message.Contains("invalid"));
        DurableExecutionHistoryFormatter.ExplanationToText(explanation)
            .Should().Contain("WARNING:")
            .And.NotContain("[interpretation]");
    }

    private sealed class Fixture : IAsyncDisposable
    {
        private Fixture(string rootPath)
        {
            RootPath = rootPath;
            RepositoryPath = Path.Combine(rootPath, "repository");
            EvidencePath = Path.Combine(rootPath, "evidence");
            KeyPath = Path.Combine(rootPath, "keys");
            Directory.CreateDirectory(RepositoryPath);
            Store = RestartStore();
        }

        public string RootPath { get; }
        public string RepositoryPath { get; }
        public string EvidencePath { get; }
        public string KeyPath { get; }
        public JsonExecutionEvidenceStore Store { get; }

        public static Fixture Create()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                $"aecs-durable-jarvis-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(root);
            return new Fixture(root);
        }

        public JsonExecutionEvidenceStore RestartStore() => new(EvidencePath, KeyPath);

        public ExecutionEvidence CreateEvidence(
            string repositoryPath,
            ContextManifest? context = null)
        {
            const string taskId = "TASK-DURABLE-001";
            const string baseline = "0123456789abcdef0123456789abcdef01234567";
            var runId = Guid.NewGuid();
            var diff = "diff --git a/src/A.cs b/src/A.cs\n+value\n";
            return new ExecutionEvidence
            {
                TaskContract = new TaskContract
                {
                    Id = taskId,
                    Objective = "Persist Jarvis explanation",
                    Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R1 }
                },
                AgentRun = new AgentRun
                {
                    Id = runId,
                    TaskId = taskId,
                    AgentType = "FixtureAgent",
                    Provider = "fixture",
                    Model = "qwen2.5-coder:7b",
                    InputTokens = 100,
                    OutputTokens = 50,
                    StartedAt = DateTime.UtcNow.AddSeconds(-2),
                    FinishedAt = DateTime.UtcNow.AddSeconds(-1)
                },
                AgentResult = new AgentRunResult
                {
                    Success = true,
                    InputTokens = 100,
                    OutputTokens = 50,
                    Duration = TimeSpan.FromSeconds(1)
                },
                AgentAttempts =
                [
                    new AgentAttemptEvidence
                    {
                        AttemptNumber = 1,
                        StartedAt = DateTime.UtcNow.AddSeconds(-2),
                        FinishedAt = DateTime.UtcNow.AddSeconds(-1),
                        Success = true,
                        DecisionReason = "completed"
                    }
                ],
                BudgetUsage = new ExecutionBudgetEvidence
                {
                    AttemptsUsed = 1,
                    MaximumAttempts = 2,
                    InputTokens = 100,
                    OutputTokens = 50,
                    WallClockElapsed = TimeSpan.FromSeconds(1),
                    WallClockLimitSeconds = 120
                },
                Baseline = new BaselineSnapshot
                {
                    Commit = baseline,
                    Branch = "dev",
                    RepositoryPath = repositoryPath,
                    CapturedAt = DateTime.UtcNow.AddSeconds(-3)
                },
                ContextManifest = context ?? RepositoryContextCompiler.EmptyManifest(
                    taskId,
                    baseline),
                CandidateChangeSet = new CandidateChangeSet
                {
                    TaskId = taskId,
                    AgentRunId = runId.ToString("N"),
                    BaselineCommit = baseline,
                    ModifiedFiles = ["src/A.cs"],
                    Diff = diff,
                    DiffHash = Hash(diff)
                },
                VerificationResults =
                [
                    new VerificationResult
                    {
                        AgentRunId = runId.ToString("N"),
                        Verifier = "Build",
                        Status = VerificationStatus.Pass,
                        Message = "Build passed"
                    }
                ],
                AcceptanceCriteriaResults =
                [
                    new AcceptanceCriterionResult
                    {
                        CriterionId = "AC-001",
                        Description = "Build succeeds",
                        Required = true,
                        EvidenceType = AcceptanceEvidenceType.Verifier,
                        EvidenceReference = "Build",
                        EvidenceReferences = ["verification-result:fixture"],
                        Status = VerificationStatus.Pass
                    }
                ],
                FinalDecision = new FinalDecisionRecord
                {
                    Decision = TaskDecision.Verified,
                    State = TaskState.Verified,
                    Reason = "all required gates passed",
                    RequiredVerifiers = ["Build"],
                    DecidedAt = DateTime.UtcNow
                }
            };
        }

        public CandidatePromotionEvidence CreatePromotion(ExecutionEvidence evidence) => new()
        {
            ExecutionEvidenceId = evidence.Id,
            CandidateId = evidence.CandidateChangeSet.Id,
            Action = CandidatePromotionAction.ExportPatch,
            Status = CandidatePromotionStatus.Exported,
            Eligibility = PromotionEligibility.Verified,
            Actor = "reviewer@example.invalid",
            ApprovalKind = PromotionApprovalKind.Policy,
            ApprovalReference = "policy/test",
            BaselineCommit = evidence.Baseline.Commit,
            DiffHash = evidence.CandidateChangeSet.DiffHash,
            RepositoryPath = evidence.Baseline.RepositoryPath,
            OutputPath = Path.Combine(RootPath, "candidate.patch"),
            Message = "exported"
        };

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(RootPath))
                Directory.Delete(RootPath, recursive: true);
            return ValueTask.CompletedTask;
        }

        private static string Hash(string value) => "sha256:" +
            Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
                .ToLowerInvariant();
    }
}

[CollectionDefinition(Name, DisableParallelization = true)]
public sealed class DurableJarvisConsoleCollection
{
    public const string Name = "durable-jarvis-console";
}
