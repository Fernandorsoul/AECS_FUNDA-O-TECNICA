using AECS.Application.AdaptiveController;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class AdaptiveControllerTests
{
    private static readonly string RepositoryPath = Path.GetTempPath();

    [Fact]
    public async Task RecommendAsync_ColdStart_UsesDeterministicFixedFallback()
    {
        var fixture = new HistoryFixture([]);
        var controller = fixture.CreateController();
        var fixedPlan = FixedPlan();

        var result = await controller.RecommendAsync(
            RepositoryPath,
            Contract("Fix null handling"),
            fixedPlan,
            CancellationToken.None);

        result.DataStatus.Should().Be(AdaptiveShadowDataStatus.ColdStart);
        result.RecommendedPlan.Model.Should().Be(fixedPlan.Model);
        result.RecommendedPlan.Budget.Should().BeSameAs(fixedPlan.Budget);
        result.SourceEvidenceIds.Should().BeEmpty();
    }

    [Fact]
    public async Task RecommendAsync_SmallSample_UsesFixedFallback()
    {
        var evidence = Enumerable.Range(0, 4)
            .Select(index => Evidence(
                $"Fix parser edge {index}",
                "small-model",
                TaskDecision.Verified,
                index))
            .ToList();
        var controller = new HistoryFixture(evidence).CreateController();

        var result = await controller.RecommendAsync(
            RepositoryPath,
            Contract("Fix another parser bug"),
            FixedPlan(),
            CancellationToken.None);

        result.DataStatus.Should().Be(AdaptiveShadowDataStatus.InsufficientSample);
        result.Inputs.MatchingRecords.Should().Be(4);
        result.RecommendedPlan.Model.Should().Be("qwen2.5-coder:7b");
    }

    [Fact]
    public async Task RecommendAsync_ReadyRecommendation_NeverExpandsBudgetOrCapabilities()
    {
        var evidence = Enumerable.Range(0, 6)
            .Select(index => Evidence(
                $"Fix distinct parser defect {index}",
                index < 3 ? "best-local-model" : "qwen2.5-coder:7b",
                TaskDecision.Verified,
                index,
                tokens: 800,
                durationSeconds: 20))
            .ToList();
        var controller = new HistoryFixture(evidence).CreateController();
        var fixedPlan = FixedPlan(new ExecutionBudget
        {
            MaxTokens = 10_000,
            MaxCostUsd = 0.20m,
            MaxRetries = 1,
            MaxDurationSeconds = 120,
            MaxFilesChanged = 10
        });

        var result = await controller.RecommendAsync(
            RepositoryPath,
            Contract("Fix parser regression"),
            fixedPlan,
            CancellationToken.None);

        result.DataStatus.Should().Be(AdaptiveShadowDataStatus.Ready);
        result.RecommendedPlan.Model.Should().Be("best-local-model");
        result.RecommendedPlan.Budget.MaxTokens.Should().BeLessThanOrEqualTo(10_000);
        result.RecommendedPlan.Budget.MaxDurationSeconds.Should().BeLessThanOrEqualTo(120);
        ExecutionCapabilityPolicyFingerprint.Create(result.RecommendedPlan.Capabilities)
            .Should().Be(ExecutionCapabilityPolicyFingerprint.Create(fixedPlan.Capabilities));
        result.SourceEvidenceIds.Should().HaveCount(6);
    }

    [Fact]
    public async Task RecommendAsync_Drift_UsesFixedFallbackAndPersistsRates()
    {
        var evidence = Enumerable.Range(0, 6)
            .Select(index => Evidence(
                $"Fix unique drift defect {index}",
                "model-a",
                index < 3 ? TaskDecision.Verified : TaskDecision.Rejected,
                index))
            .ToList();
        var controller = new HistoryFixture(evidence).CreateController();

        var result = await controller.RecommendAsync(
            RepositoryPath,
            Contract("Fix drift defect"),
            FixedPlan(),
            CancellationToken.None);

        result.DataStatus.Should().Be(AdaptiveShadowDataStatus.DriftDetected);
        result.Inputs.OlderSuccessRate.Should().Be(1d);
        result.Inputs.RecentSuccessRate.Should().Be(0d);
        result.RecommendedPlan.Model.Should().Be("qwen2.5-coder:7b");
    }

    [Fact]
    public async Task RecommendAsync_ContradictoryHistory_UsesFixedFallback()
    {
        var evidence = Enumerable.Range(0, 5)
            .Select(index => Evidence(
                "Fix identical parser defect",
                "model-a",
                index % 2 == 0 ? TaskDecision.Verified : TaskDecision.Rejected,
                index))
            .ToList();
        var controller = new HistoryFixture(evidence).CreateController();

        var result = await controller.RecommendAsync(
            RepositoryPath,
            Contract("Fix another parser defect"),
            FixedPlan(),
            CancellationToken.None);

        result.DataStatus.Should().Be(AdaptiveShadowDataStatus.ContradictoryHistory);
        result.RecommendedPlan.Model.Should().Be("qwen2.5-coder:7b");
    }

    [Fact]
    public async Task RecommendAsync_IncompleteAndNonReproducibleHistory_IsExcluded()
    {
        var incomplete = Evidence("Fix incomplete", "model-a", TaskDecision.Verified, 0,
            state: TaskState.Running);
        var nonReproducible = Evidence("Fix missing hash", "model-a",
            TaskDecision.Verified, 1, manifestHash: string.Empty);
        var fixture = new HistoryFixture(
            [incomplete, nonReproducible],
            ["An evidence record was invalid and was omitted."]);

        var result = await fixture.CreateController().RecommendAsync(
            RepositoryPath,
            Contract("Fix current defect"),
            FixedPlan(),
            CancellationToken.None);

        result.DataStatus.Should().Be(AdaptiveShadowDataStatus.InvalidHistory);
        result.Inputs.ExcludedIncompleteRecords.Should().Be(1);
        result.Inputs.ExcludedNonReproducibleRecords.Should().Be(1);
        result.Inputs.InvalidOrTamperedRecords.Should().Be(1);
        result.SourceEvidenceIds.Should().BeEmpty();
    }

    [Fact]
    public async Task CreateReportAsync_GroupsOnlyShadowEvidenceByRiskAndTaskType()
    {
        var included = Evidence("Fix report bug", "model-a", TaskDecision.Verified, 0,
            shadow: Shadow(RiskLevel.R1, "bugfix"));
        var legacy = Evidence("Add report feature", "model-b", TaskDecision.Rejected, 1);
        var controller = new HistoryFixture([included, legacy]).CreateController();

        var report = await controller.CreateReportAsync(
            RepositoryPath,
            100,
            CancellationToken.None);

        report.AuthenticatedRecords.Should().Be(2);
        report.IncludedShadowRecords.Should().Be(1);
        report.ExcludedRecords.Should().Be(1);
        report.Groups.Should().ContainSingle().Which.Should().Match<AdaptiveShadowReportGroup>(
            group => group.Risk == RiskLevel.R1 &&
                     group.TaskType == "bugfix" &&
                     group.Executions == 1 &&
                     group.VerifiedExecutions == 1);
    }

    private static TaskContract Contract(string objective) => new()
    {
        Id = "TASK-34",
        Objective = objective,
        Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R1 },
        Budget = ExecutionBudget.Default
    };

    private static ExecutionPlan FixedPlan(ExecutionBudget? budget = null) => new()
    {
        TaskId = "TASK-34",
        Model = "qwen2.5-coder:7b",
        Risk = RiskLevel.R1,
        Budget = budget ?? ExecutionBudget.Default,
        Verification = new VerificationProfile(),
        Capabilities = ExecutionCapabilityPolicy.RestrictiveDefault()
    };

    private static ExecutionEvidence Evidence(
        string objective,
        string model,
        TaskDecision decision,
        int order,
        int tokens = 1000,
        int durationSeconds = 30,
        TaskState? state = null,
        string manifestHash = "sha256:context",
        AdaptiveShadowEvidence? shadow = null)
    {
        var taskId = $"history-{order}";
        return new ExecutionEvidence
        {
            Id = Guid.NewGuid(),
            TaskContract = new TaskContract
            {
                Id = taskId,
                Objective = objective,
                Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R1 }
            },
            AgentRun = new AgentRun
            {
                Id = Guid.NewGuid(),
                TaskId = taskId,
                Model = model,
                InputTokens = tokens / 2,
                OutputTokens = tokens - tokens / 2
            },
            AgentResult = new AgentRunResult
            {
                Success = decision == TaskDecision.Verified,
                Duration = TimeSpan.FromSeconds(durationSeconds)
            },
            Baseline = new BaselineSnapshot
            {
                RepositoryPath = RepositoryPath,
                Commit = new string('a', 40)
            },
            ContextManifest = new ContextManifest
            {
                Strategy = ContextManifestSchema.GraphStrategyId,
                ManifestHash = manifestHash
            },
            CandidateChangeSet = new CandidateChangeSet
            {
                DiffHash = "sha256:diff"
            },
            FinalDecision = new FinalDecisionRecord
            {
                Decision = decision,
                State = state ?? (decision == TaskDecision.Verified
                    ? TaskState.Verified
                    : TaskState.Rejected)
            },
            AdaptiveShadow = shadow,
            CreatedAt = DateTime.UnixEpoch.AddMinutes(order)
        };
    }

    private static AdaptiveShadowEvidence Shadow(RiskLevel risk, string taskType) => new()
    {
        Recommendation = new AdaptiveShadowRecommendation
        {
            DataStatus = AdaptiveShadowDataStatus.Ready,
            Inputs = new AdaptiveShadowInputs { Risk = risk, TaskType = taskType }
        },
        Evaluation = new AdaptiveShadowEvaluation
        {
            FixedControllerDecision = TaskDecision.Verified,
            DurationSeconds = 10,
            RecommendationAgreedWithFixedModel = true
        }
    };

    private sealed class HistoryFixture : IExecutionEvidenceStore, IEvidenceGraphSource
    {
        private readonly Dictionary<Guid, ExecutionEvidence> _evidence;
        private readonly List<string> _diagnostics;

        public HistoryFixture(
            IEnumerable<ExecutionEvidence> evidence,
            IEnumerable<string>? diagnostics = null)
        {
            _evidence = evidence.ToDictionary(item => item.Id);
            _diagnostics = diagnostics?.ToList() ?? [];
        }

        public AdaptiveController CreateController() => new(this, this);

        public void EnsureRepositoryIsolation(string repositoryPath) { }

        public Task<ExecutionEvidence?> LoadAsync(
            Guid evidenceId,
            CancellationToken cancellationToken) => Task.FromResult(
                _evidence.GetValueOrDefault(evidenceId));

        public Task<EvidenceGraphQueryResult> QueryEvidenceGraphsAsync(
            EvidenceGraphQuery query,
            EvidenceReadScope scope,
            CancellationToken cancellationToken) => Task.FromResult(new EvidenceGraphQueryResult
            {
                Items = _evidence.Values
                    .OrderByDescending(item => item.CreatedAt)
                    .Take(query.Limit)
                    .Select(item => new EvidenceGraphSummary
                    {
                        EvidenceId = item.Id,
                        CreatedAt = item.CreatedAt
                    })
                    .ToList(),
                Diagnostics = _diagnostics
            });

        public Task<EvidenceGraph?> LoadEvidenceGraphAsync(
            Guid evidenceId,
            EvidenceReadScope scope,
            CancellationToken cancellationToken) => Task.FromResult<EvidenceGraph?>(null);

        public Task<string> SaveAsync(
            ExecutionEvidence evidence,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AppendPromotionAsync(
            Guid evidenceId,
            CandidatePromotionEvidence promotion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AppendReplayAsync(
            Guid evidenceId,
            ExecutionReplayEvidence replay,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
