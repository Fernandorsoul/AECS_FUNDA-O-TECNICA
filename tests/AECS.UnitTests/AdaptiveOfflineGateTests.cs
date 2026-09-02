using System.Text.Json;
using System.Text.Json.Nodes;
using AECS.Application;
using AECS.Application.AdaptiveController;
using AECS.Application.Experiments;
using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class AdaptiveOfflineGateTests
{
    [Fact]
    public void DatasetContract_RejectsFewerThanFiftyDistinctTasks()
    {
        using var fixture = new AdaptiveOfflineFixture();
        var invalid = fixture.CopyManifest(fixture.Manifest.Tasks.Take(49).ToList());

        var action = () => AdaptiveOfflineDatasetContract.Validate(invalid);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void DatasetLoader_RejectsUnknownProperties()
    {
        using var fixture = new AdaptiveOfflineFixture();
        var json = JsonSerializer.Serialize(
            fixture.Manifest,
            AdaptiveOfflineDatasetLoader.SerializerOptions);
        json = json.Insert(1, "\n  \"unexpected\": true,");
        var path = Path.Combine(fixture.Root, "unknown-property.json");
        File.WriteAllText(path, json);

        var action = () => AdaptiveOfflineDatasetLoader.Load(path);

        action.Should().Throw<JsonException>();
    }

    [Fact]
    public void DatasetLoader_RejectsMissingRequiredProviderKind()
    {
        using var fixture = new AdaptiveOfflineFixture();
        var manifest = JsonNode.Parse(JsonSerializer.Serialize(
            fixture.Manifest,
            AdaptiveOfflineDatasetLoader.SerializerOptions))!.AsObject();
        manifest["provider"]!.AsObject().Remove("kind");
        var path = Path.Combine(fixture.Root, "missing-provider-kind.json");
        File.WriteAllText(path, manifest.ToJsonString());

        var action = () => AdaptiveOfflineDatasetLoader.Load(path);

        action.Should().Throw<JsonException>();
    }

    [Fact]
    public void DatasetLoader_RejectsNumericEnums()
    {
        using var fixture = new AdaptiveOfflineFixture();
        var manifest = JsonNode.Parse(JsonSerializer.Serialize(
            fixture.Manifest,
            AdaptiveOfflineDatasetLoader.SerializerOptions))!.AsObject();
        manifest["provider"]!["kind"] = 0;
        var path = Path.Combine(fixture.Root, "numeric-provider-kind.json");
        File.WriteAllText(path, manifest.ToJsonString());

        var action = () => AdaptiveOfflineDatasetLoader.Load(path);

        action.Should().Throw<JsonException>();
    }

    [Fact]
    public async Task Preflight_AcceptsAuthenticatedSourcesStrictlyBeforeCutoff()
    {
        using var fixture = new AdaptiveOfflineFixture();
        var definition = fixture.Manifest.Tasks[0];

        var result = await fixture.CreatePreflight().PrepareAsync(
            fixture.Loaded,
            definition,
            fixture.Contracts[definition.Id],
            CancellationToken.None);

        result.SourceEvidenceIds.Should().HaveCount(5);
        result.FixedContextStrategy.Should().Be("graph-ranked");
        result.RecommendedPlan.Budget.MaxTokens.Should()
            .BeLessThan(result.FixedPlan.Budget.MaxTokens);
        result.FixedContract.ContractFingerprint.Should().NotBeNullOrWhiteSpace();
        result.RecommendedContract.ContractFingerprint.Should().NotBe(
            result.FixedContract.ContractFingerprint);
    }

    [Fact]
    public async Task Preflight_RejectsSourceAtOrAfterPreregisteredCutoff()
    {
        using var fixture = new AdaptiveOfflineFixture(sourceAfterCutoff: true);
        var definition = fixture.Manifest.Tasks[0];

        var action = () => fixture.CreatePreflight().PrepareAsync(
            fixture.Loaded,
            definition,
            fixture.Contracts[definition.Id],
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*temporal or integrity preflight*");
    }

    [Fact]
    public async Task Preflight_RejectsRecommendationThatExpandsBudget()
    {
        using var fixture = new AdaptiveOfflineFixture(expandBudget: true);
        var definition = fixture.Manifest.Tasks[0];

        var action = () => fixture.CreatePreflight().PrepareAsync(
            fixture.Loaded,
            definition,
            fixture.Contracts[definition.Id],
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*expands the fixed execution budget*");
    }

    [Fact]
    public async Task Runner_ExecutesPairedArmsAndResumeDoesNotExecuteAgain()
    {
        using var fixture = new AdaptiveOfflineFixture();
        var executor = new RecordingExecutor();
        var runner = new AdaptiveOfflineRunner(fixture.CreatePreflight(), executor);
        var artifacts = new AdaptiveOfflineArtifactStore(fixture.Output);

        var initial = await runner.RunAsync(
            fixture.Loaded,
            fixture.Baseline,
            artifacts,
            resume: false,
            CancellationToken.None);
        var callsAfterInitialRun = executor.Calls;
        var resumed = await runner.RunAsync(
            fixture.Loaded,
            fixture.Baseline,
            artifacts,
            resume: true,
            CancellationToken.None);

        callsAfterInitialRun.Should().Be(100);
        executor.Calls.Should().Be(callsAfterInitialRun);
        initial.Pairs.Should().HaveCount(50).And.OnlyContain(pair =>
            pair.Status == AdaptiveOfflineResultStatus.Completed);
        initial.Analysis.DistinctCompletedTasks.Should().Be(50);
        initial.Analysis.Conclusion.Should().Be(HypothesisConclusion.Maintain);
        resumed.Pairs.Select(pair => pair.PairKey).Should()
            .Equal(initial.Pairs.Select(pair => pair.PairKey));
        File.Exists(artifacts.ReportPath).Should().BeTrue();
    }

    [Fact]
    public async Task Runner_PersistsDatasetIdentityWhenPreflightFails()
    {
        using var fixture = new AdaptiveOfflineFixture(expandBudget: true);
        var executor = new RecordingExecutor();
        var runner = new AdaptiveOfflineRunner(fixture.CreatePreflight(), executor);

        var result = await runner.RunAsync(
            fixture.Loaded,
            fixture.Baseline,
            new AdaptiveOfflineArtifactStore(fixture.Output),
            resume: false,
            CancellationToken.None);

        executor.Calls.Should().Be(0);
        result.Pairs.Should().OnlyContain(pair =>
            pair.DatasetId == fixture.Manifest.Id &&
            pair.Status == AdaptiveOfflineResultStatus.Failed);
    }

    [Fact]
    public async Task Runner_RejectsArmExecutedByDifferentProviderAdapter()
    {
        using var fixture = new AdaptiveOfflineFixture();
        var runner = new AdaptiveOfflineRunner(
            fixture.CreatePreflight(),
            new RecordingExecutor("CloudAdapter"));

        var result = await runner.RunAsync(
            fixture.Loaded,
            fixture.Baseline,
            new AdaptiveOfflineArtifactStore(fixture.Output),
            resume: false,
            CancellationToken.None);

        result.Pairs.Should().OnlyContain(pair =>
            pair.Status == AdaptiveOfflineResultStatus.Failed &&
            pair.Failure.Contains("different provider adapter", StringComparison.Ordinal));
    }

    [Fact]
    public void Analyzer_KeepsMissingPairsInDenominatorAndAdjusts()
    {
        using var fixture = new AdaptiveOfflineFixture();

        var result = AdaptiveOfflineAnalyzer.Analyze(fixture.Manifest, []);

        result.PlannedPairs.Should().Be(50);
        result.CompletedPairs.Should().Be(0);
        result.Conclusion.Should().Be(HypothesisConclusion.Adjust);
        result.ConclusionReason.Should().Contain("0/50 pairs");
    }

    private sealed class AdaptiveOfflineFixture : IDisposable
    {
        private static readonly DateTime SourceTime =
            new(2026, 1, 1, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime Cutoff =
            new(2026, 1, 2, 12, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime RecommendationTime =
            new(2026, 1, 2, 13, 0, 0, DateTimeKind.Utc);
        private static readonly DateTime PreregisteredTime =
            new(2026, 1, 3, 12, 0, 0, DateTimeKind.Utc);
        private readonly EvidenceFixture _evidence;

        public AdaptiveOfflineFixture(bool sourceAfterCutoff = false, bool expandBudget = false)
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "aecs-adaptive-offline-tests",
                Guid.NewGuid().ToString("N"));
            Repository = Path.Combine(Root, "repository");
            Output = Path.Combine(Root, "output");
            Directory.CreateDirectory(Repository);
            Contracts = new Dictionary<string, TaskContract>(StringComparer.Ordinal);
            _evidence = new EvidenceFixture(Repository);

            var parser = new TaskContractParser();
            var definitions = new List<AdaptiveOfflineTaskDefinition>();
            var sourceIds = Enumerable.Range(0, 5).Select(index =>
            {
                var source = SourceEvidence(
                    index,
                    sourceAfterCutoff && index == 0
                        ? Cutoff
                        : SourceTime.AddMinutes(index));
                _evidence.Add(source, Hash('c'));
                return source.Id;
            }).ToList();

            foreach (var index in Enumerable.Range(1, 50))
            {
                var id = $"ADAPT-{index:000}";
                var relativePath = $"tasks/{id}.yaml";
                var fullPath = Path.Combine(
                    Repository,
                    relativePath.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
                File.WriteAllText(fullPath, ContractYaml(id));
                var contract = parser.ParseFromFile(fullPath);
                Contracts[id] = contract;
                var recommendation = RecommendationEvidence(
                    contract,
                    sourceIds,
                    expandBudget);
                var evidenceHash = Hash('d');
                _evidence.Add(recommendation, evidenceHash);
                definitions.Add(new AdaptiveOfflineTaskDefinition
                {
                    Id = id,
                    ContractPath = relativePath,
                    ExpectedDecision = TaskDecision.Verified,
                    Seed = 7000 + index,
                    HistoryCutoffUtc = Cutoff,
                    RecommendationEvidenceId = recommendation.Id,
                    RecommendationEvidenceHash = evidenceHash
                });
            }

            Manifest = CreateManifest(definitions);
            Loaded = new LoadedAdaptiveOfflineDataset
            {
                ManifestPath = Path.Combine(Root, "dataset.json"),
                RepositoryPath = Repository,
                Manifest = Manifest
            };
        }

        public string Root { get; }
        public string Repository { get; }
        public string Output { get; }
        public string Baseline { get; } = new('a', 40);
        public Dictionary<string, TaskContract> Contracts { get; }
        public AdaptiveOfflineDatasetManifest Manifest { get; }
        public LoadedAdaptiveOfflineDataset Loaded { get; }

        public AdaptiveOfflinePreflight CreatePreflight() => new(_evidence, _evidence);

        public AdaptiveOfflineDatasetManifest CopyManifest(
            List<AdaptiveOfflineTaskDefinition> tasks) => CreateManifest(tasks);

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private AdaptiveOfflineDatasetManifest CreateManifest(
            List<AdaptiveOfflineTaskDefinition> tasks) => new()
            {
                SchemaVersion = AdaptiveOfflineSchema.DatasetVersion,
                Id = "adaptive-offline-test",
                Version = "1.0.0",
                PreregisteredAtUtc = PreregisteredTime,
                Repository = new AdaptiveOfflineRepositoryDefinition
                {
                    Path = "repository",
                    Baseline = Baseline,
                    SourceUri = "https://example.invalid/aecs-evaluation.git",
                    LicenseSpdx = "MIT",
                    RedistributionAllowed = true
                },
                Provider = new AdaptiveOfflineProviderDefinition
                {
                    Kind = AdaptiveOfflineProvider.Local,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["baseUrl"] = "http://localhost:11434",
                        ["contextWindowTokens"] = "8192"
                    }
                },
                Repetitions = 1,
                Protocol = new AdaptiveOfflineProtocol
                {
                    Design = AdaptiveOfflineSchema.Design,
                    HypothesisId = AdaptiveOfflineSchema.HypothesisId,
                    Hypothesis = "A bounded adaptive recommendation improves verified changes per effective cost.",
                    PrimaryMetric = AdaptiveOfflineSchema.PrimaryMetric,
                    MinimumDistinctTasks = 50,
                    ConfidenceLevel = 0.95,
                    ZeroReferencePolicy = ZeroReferencePolicy.AbsolutePairedDelta,
                    DeathCriteria = new AdaptiveOfflineDeathCriteria
                    {
                        MinimumRelativeImprovement = 0.10,
                        MinimumAbsoluteImprovement = 1,
                        MaximumCandidateFailureRate = 0,
                        MaximumCandidateScopeViolationRate = 0,
                        MaximumCandidateSecurityViolationRate = 0,
                        MaximumMedianLatencyRegressionRate = 0.50
                    }
                },
                Tasks = tasks
            };

        private ExecutionEvidence SourceEvidence(int index, DateTime createdAt)
        {
            var taskId = $"history-{index}";
            var task = TaskContractIntegrity.Seal(new TaskContract
            {
                Id = taskId,
                Objective = $"Fix historical parser defect {index}",
                Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R1 }
            });
            return Evidence(task, createdAt, adaptiveShadow: null);
        }

        private ExecutionEvidence RecommendationEvidence(
            TaskContract contract,
            List<Guid> sourceIds,
            bool expandBudget)
        {
            var fixedBudget = contract.Budget;
            var recommendedBudget = new ExecutionBudget
            {
                MaxTokens = expandBudget ? fixedBudget.MaxTokens + 1 : fixedBudget.MaxTokens - 1000,
                MaxCostUsd = fixedBudget.MaxCostUsd - 0.001m,
                MaxRetries = fixedBudget.MaxRetries,
                MaxDurationSeconds = fixedBudget.MaxDurationSeconds - 5,
                MaxFilesChanged = fixedBudget.MaxFilesChanged
            };
            var capabilities = contract.Execution.EffectiveCapabilities;
            var shadow = new AdaptiveShadowEvidence
            {
                Recommendation = new AdaptiveShadowRecommendation
                {
                    DataStatus = AdaptiveShadowDataStatus.Ready,
                    Inputs = new AdaptiveShadowInputs
                    {
                        Risk = contract.Constraints.SecurityRisk,
                        TaskType = AdaptiveTaskTypeClassifier.Classify(contract.Objective),
                        ObjectiveFingerprint =
                            AdaptiveTaskTypeClassifier.Fingerprint(contract.Objective),
                        AuthenticatedRecords = sourceIds.Count,
                        EligibleRecords = sourceIds.Count,
                        MatchingRecords = sourceIds.Count
                    },
                    FixedPlan = new AdaptiveShadowPlan
                    {
                        Model = "model-fixed",
                        ContextStrategy = "governed-by-context-compiler",
                        Budget = fixedBudget,
                        Verification = contract.Verification,
                        Capabilities = capabilities
                    },
                    RecommendedPlan = new AdaptiveShadowPlan
                    {
                        Model = "model-recommended",
                        ContextStrategy = "graph-ranked+textual-file-inventory",
                        Budget = recommendedBudget,
                        Verification = contract.Verification,
                        Capabilities = capabilities
                    },
                    SourceEvidenceIds = sourceIds,
                    GeneratedAt = RecommendationTime
                },
                Evaluation = new AdaptiveShadowEvaluation
                {
                    FixedControllerDecision = TaskDecision.Verified,
                    FixedControllerState = TaskState.Verified,
                    FixedControllerSucceeded = true,
                    ExecutedModel = "model-fixed",
                    ExecutedContextStrategy = "graph-ranked+textual-file-inventory",
                    ExecutedBudget = fixedBudget,
                    FixedPlanPreserved = true,
                    CounterfactualExecuted = false
                }
            };
            return Evidence(contract, RecommendationTime.AddMinutes(1), shadow);
        }

        private ExecutionEvidence Evidence(
            TaskContract contract,
            DateTime createdAt,
            AdaptiveShadowEvidence? adaptiveShadow)
        {
            var runId = Guid.NewGuid();
            return new ExecutionEvidence
            {
                Id = Guid.NewGuid(),
                TaskContract = contract,
                AgentRun = new AgentRun
                {
                    Id = runId,
                    TaskId = contract.Id,
                    Model = "model-fixed"
                },
                AgentResult = new AgentRunResult { Success = true },
                Baseline = new BaselineSnapshot
                {
                    RepositoryPath = Repository,
                    Commit = Baseline
                },
                ContextManifest = new ContextManifest
                {
                    Strategy = "graph-ranked+textual-file-inventory",
                    ManifestHash = Hash('e')
                },
                CandidateChangeSet = new CandidateChangeSet
                {
                    TaskId = contract.Id,
                    AgentRunId = runId.ToString("N"),
                    BaselineCommit = Baseline,
                    ModifiedFiles = ["result.txt"],
                    Diff = "diff",
                    DiffHash = Hash('f')
                },
                FinalDecision = new FinalDecisionRecord
                {
                    Decision = TaskDecision.Verified,
                    State = TaskState.Verified
                },
                AdaptiveShadow = adaptiveShadow,
                CreatedAt = createdAt
            };
        }

        private static string ContractYaml(string id) => $$"""
            schema_version: aecs.task-contract/v1
            task:
              id: {{id}}
              objective: Fix parser regression safely
              acceptance: []
              scope:
                allowed:
                  - result.txt
                forbidden: []
              constraints:
                security_risk: low
                database_migration: false
                external_dependency: false
              budget:
                tokens: 5000
                usd: 0.01
                retries: 0
                wall_clock_seconds: 30
                max_files_changed: 1
              execution:
                runtime: host
                working_directory: .
              verification:
                build: disabled
                unit_tests: disabled
                integration_tests: disabled
                scope: required
                security_scan: disabled
                architecture: disabled
                critical_semantic_failures: disabled
              approval:
                production: none
            """;

        private static string Hash(char character) => "sha256:" + new string(character, 64);
    }

    private sealed class EvidenceFixture : IExecutionEvidenceStore, IEvidenceGraphSource
    {
        private readonly string _repositoryPath;
        private readonly Dictionary<Guid, ExecutionEvidence> _evidence = [];
        private readonly Dictionary<Guid, string> _hashes = [];

        public EvidenceFixture(string repositoryPath) => _repositoryPath = repositoryPath;

        public void Add(ExecutionEvidence evidence, string evidenceHash)
        {
            _evidence.Add(evidence.Id, evidence);
            _hashes.Add(evidence.Id, evidenceHash);
        }

        public void EnsureRepositoryIsolation(string repositoryPath) =>
            Path.GetFullPath(repositoryPath).Should().Be(Path.GetFullPath(_repositoryPath));

        public Task<ExecutionEvidence?> LoadAsync(
            Guid evidenceId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_evidence.GetValueOrDefault(evidenceId));

        public Task<EvidenceGraph?> LoadEvidenceGraphAsync(
            Guid evidenceId,
            EvidenceReadScope scope,
            CancellationToken cancellationToken)
        {
            var evidence = _evidence.GetValueOrDefault(evidenceId);
            return Task.FromResult(evidence is null ? null : new EvidenceGraph
            {
                EvidenceId = evidence.Id,
                RepositoryPath = _repositoryPath,
                Principal = scope.Principal,
                Summary = new EvidenceGraphSummary
                {
                    EvidenceId = evidence.Id,
                    EvidenceHash = _hashes[evidence.Id],
                    CreatedAt = evidence.CreatedAt,
                    BaselineCommit = evidence.Baseline.Commit,
                    TaskId = evidence.TaskContract.Id,
                    RunId = evidence.AgentRun.Id,
                    Decision = evidence.FinalDecision.Decision
                }
            });
        }

        public Task<EvidenceGraphQueryResult> QueryEvidenceGraphsAsync(
            EvidenceGraphQuery query,
            EvidenceReadScope scope,
            CancellationToken cancellationToken) => throw new NotSupportedException();

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

    private sealed class RecordingExecutor : IAdaptiveOfflineArmExecutor
    {
        private readonly string _adapter;

        public RecordingExecutor(string adapter = "OllamaAdapter") => _adapter = adapter;

        public int Calls { get; private set; }

        public Task<StagedExecutionResult> ExecuteAsync(
            AdaptiveOfflineArmExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            var recommended = request.Arm == AdaptiveOfflineArm.Recommended;
            var evidenceId = Guid.NewGuid();
            var decision = recommended ? TaskDecision.Verified : TaskDecision.Rejected;
            return Task.FromResult(new StagedExecutionResult
            {
                Contract = request.Contract,
                Model = request.Plan.Model,
                AgentRun = new AgentRun
                {
                    Id = Guid.NewGuid(),
                    TaskId = request.Task.Id,
                    Model = request.Plan.Model,
                    RetryCount = 0
                },
                AgentResult = new AgentRunResult
                {
                    Success = recommended,
                    InputTokens = 100,
                    OutputTokens = 20,
                    EstimatedCost = 0.01m,
                    UsageAccounting = new AgentUsageAccounting
                    {
                        Adapter = "OllamaAdapter",
                        Model = request.Plan.Model,
                        LocalResourceEstimatedCostUsd = 0.01m,
                        CostComplete = true
                    }
                },
                BudgetUsage = new ExecutionBudgetEvidence
                {
                    WallClockElapsed = recommended
                        ? TimeSpan.FromSeconds(1)
                        : TimeSpan.FromSeconds(2),
                    WallClockLimitSeconds = request.Plan.Budget.MaxDurationSeconds,
                    MaximumAttempts = request.Plan.Budget.MaxRetries + 1,
                    AttemptsUsed = 1
                },
                Baseline = new BaselineSnapshot
                {
                    RepositoryPath = request.RepositoryPath,
                    Commit = request.BaselineCommit
                },
                ContextManifest = new ContextManifest
                {
                    Adapter = _adapter,
                    Strategy = request.ContextStrategy + "+textual-file-inventory",
                    ManifestHash = "sha256:" + new string('a', 64)
                },
                CandidateChangeSet = new CandidateChangeSet
                {
                    TaskId = request.Task.Id,
                    BaselineCommit = request.BaselineCommit,
                    ModifiedFiles = recommended ? ["result.txt"] : [],
                    Diff = recommended ? "diff" : string.Empty,
                    DiffHash = "sha256:" + new string('b', 64)
                },
                Decision = new DecisionResult
                {
                    Decision = decision,
                    TargetState = recommended ? TaskState.Verified : TaskState.Rejected
                },
                FinalState = recommended ? TaskState.Verified : TaskState.Rejected,
                EvidenceId = evidenceId,
                EvidenceLocation = $"evidence://{evidenceId:N}",
                OriginalRepositoryUnchanged = true
            });
        }
    }
}
