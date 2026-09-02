using AECS.Application.Experiments;
using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.AdaptiveController;

public enum AdaptiveOfflineArm
{
    Fixed,
    Recommended
}

public enum AdaptiveOfflineResultStatus
{
    Completed,
    Failed
}

public sealed class AdaptiveOfflineArmExecutionRequest
{
    public AdaptiveOfflineArm Arm { get; init; }
    public string DatasetId { get; init; } = string.Empty;
    public string DatasetHash { get; init; } = string.Empty;
    public string PairKey { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public AdaptiveOfflineProviderDefinition Provider { get; init; } = new();
    public AdaptiveOfflineTaskDefinition Task { get; init; } = new();
    public int Repetition { get; init; }
    public int EffectiveSeed { get; init; }
    public TaskContract Contract { get; init; } = new();
    public AdaptiveShadowPlan Plan { get; init; } = new();
    public string ContextStrategy { get; init; } = string.Empty;
}

public interface IAdaptiveOfflineArmExecutor
{
    Task<StagedExecutionResult> ExecuteAsync(
        AdaptiveOfflineArmExecutionRequest request,
        CancellationToken cancellationToken);
}

public sealed class DelegatingAdaptiveOfflineArmExecutor : IAdaptiveOfflineArmExecutor
{
    private readonly Func<AdaptiveOfflineArmExecutionRequest, CancellationToken,
        Task<StagedExecutionResult>> _execute;

    public DelegatingAdaptiveOfflineArmExecutor(
        Func<AdaptiveOfflineArmExecutionRequest, CancellationToken,
            Task<StagedExecutionResult>> execute)
    {
        ArgumentNullException.ThrowIfNull(execute);
        _execute = execute;
    }

    public Task<StagedExecutionResult> ExecuteAsync(
        AdaptiveOfflineArmExecutionRequest request,
        CancellationToken cancellationToken) => _execute(request, cancellationToken);
}

public sealed class AdaptiveOfflineExecutionController : IExecutionController
{
    private readonly AdaptiveShadowPlan _plan;

    public AdaptiveOfflineExecutionController(AdaptiveShadowPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        _plan = plan;
    }

    public Task<ExecutionPlan> PlanAsync(
        TaskContract task,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(task);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ExecutionPlan
        {
            TaskId = task.Id,
            Model = _plan.Model,
            Budget = _plan.Budget,
            Risk = task.Constraints.SecurityRisk,
            Verification = _plan.Verification,
            Capabilities = _plan.Capabilities
        });
    }
}

public sealed class AdaptiveOfflineArmResult
{
    public AdaptiveOfflineArm Arm { get; init; }
    public AdaptiveOfflineResultStatus Status { get; init; }
    public string Failure { get; init; } = string.Empty;
    public string Adapter { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string ContextStrategy { get; init; } = string.Empty;
    public ExecutionBudget Budget { get; init; } = ExecutionBudget.Default;
    public TaskDecision? Decision { get; init; }
    public bool MatchesExpected { get; init; }
    public bool VerifiedCodeChange { get; init; }
    public bool FirstPassVerified { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCostUsd { get; init; }
    public decimal? EffectiveCostUsd { get; init; }
    public string CostBasis { get; init; } = string.Empty;
    public double DurationSeconds { get; init; }
    public int RetryCount { get; init; }
    public int ScopeViolationCount { get; init; }
    public int SecurityViolationCount { get; init; }
    public Guid? EvidenceId { get; init; }
    public string EvidenceLocation { get; init; } = string.Empty;
    public bool OriginalRepositoryUnchanged { get; init; }
}

public sealed class AdaptiveOfflinePairResult
{
    public string PairKey { get; init; } = string.Empty;
    public AdaptiveOfflineResultStatus Status { get; init; }
    public string Failure { get; init; } = string.Empty;
    public string DatasetId { get; init; } = string.Empty;
    public string DatasetHash { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public int Repetition { get; init; }
    public int EffectiveSeed { get; init; }
    public Guid RecommendationEvidenceId { get; init; }
    public string RecommendationEvidenceHash { get; init; } = string.Empty;
    public List<Guid> SourceEvidenceIds { get; init; } = [];
    public AdaptiveOfflineArmResult Fixed { get; init; } = new();
    public AdaptiveOfflineArmResult Recommended { get; init; } = new();
}

public sealed class AdaptiveOfflineAnalysis
{
    public string HypothesisId { get; init; } = string.Empty;
    public HypothesisConclusion Conclusion { get; init; }
    public string ConclusionReason { get; init; } = string.Empty;
    public int PlannedPairs { get; init; }
    public int ObservedPairs { get; init; }
    public int CompletedPairs { get; init; }
    public int DistinctCompletedTasks { get; init; }
    public double CandidateFailureRate { get; init; }
    public double CandidateScopeViolationRate { get; init; }
    public double CandidateSecurityViolationRate { get; init; }
    public double? ReferenceVccPerEffectiveDollar { get; init; }
    public double? CandidateVccPerEffectiveDollar { get; init; }
    public double? RelativePrimaryMetricImprovement { get; init; }
    public double? MedianLatencyRegressionRate { get; init; }
    public ExperimentMetricDistribution PairedPrimaryMetricDelta { get; init; } = new();
    public ExperimentMetricDistribution VerifiedCodeChangeDelta { get; init; } = new();
    public ExperimentMetricDistribution TokenDelta { get; init; } = new();
    public ExperimentMetricDistribution LatencyDeltaSeconds { get; init; } = new();
}

public sealed class AdaptiveOfflineReport
{
    public string SchemaVersion { get; init; } = AdaptiveOfflineSchema.ReportVersion;
    public string DatasetId { get; init; } = string.Empty;
    public string DatasetVersion { get; init; } = string.Empty;
    public string DatasetHash { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public DateTime ExecutedAtUtc { get; init; } = DateTime.UtcNow;
    public List<AdaptiveOfflinePairResult> Pairs { get; init; } = [];
    public AdaptiveOfflineAnalysis Analysis { get; init; } = new();
}

public sealed class AdaptiveOfflineRunner
{
    private readonly AdaptiveOfflinePreflight _preflight;
    private readonly IAdaptiveOfflineArmExecutor _executor;
    private readonly TaskContractParser _parser = new();

    public AdaptiveOfflineRunner(
        AdaptiveOfflinePreflight preflight,
        IAdaptiveOfflineArmExecutor executor)
    {
        ArgumentNullException.ThrowIfNull(preflight);
        ArgumentNullException.ThrowIfNull(executor);
        _preflight = preflight;
        _executor = executor;
    }

    public async Task<AdaptiveOfflineReport> RunAsync(
        LoadedAdaptiveOfflineDataset dataset,
        string baselineCommit,
        AdaptiveOfflineArtifactStore artifacts,
        bool resume,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentException.ThrowIfNullOrWhiteSpace(baselineCommit);
        ArgumentNullException.ThrowIfNull(artifacts);
        AdaptiveOfflineDatasetContract.Validate(dataset.Manifest);
        if (!AdaptiveOfflineDatasetContract.BaselineMatches(
                dataset.Manifest.Repository.Baseline,
                baselineCommit))
        {
            throw new InvalidOperationException(
                "Adaptive offline repository HEAD does not match the preregistered baseline.");
        }
        AdaptiveOfflineArtifactStore.EnsureOutsideRepository(
            artifacts.OutputDirectory,
            dataset.RepositoryPath);

        var datasetHash = AdaptiveOfflineDatasetFingerprint.Create(
            dataset.Manifest,
            baselineCommit);
        await artifacts.InitializeAsync(datasetHash, resume, cancellationToken);
        var pairs = new List<AdaptiveOfflinePairResult>();

        foreach (var task in dataset.Manifest.Tasks.OrderBy(item => item.Id, StringComparer.Ordinal))
        {
            foreach (var repetition in Enumerable.Range(1, dataset.Manifest.Repetitions))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var pairKey = AdaptiveOfflineDatasetFingerprint.PairKey(
                    datasetHash,
                    task.Id,
                    repetition);
                var checkpoint = resume
                    ? await artifacts.LoadAsync(pairKey, datasetHash, cancellationToken)
                    : null;
                if (checkpoint is not null)
                {
                    pairs.Add(checkpoint);
                    continue;
                }

                var pair = await ExecutePairAsync(
                    dataset,
                    datasetHash,
                    baselineCommit,
                    task,
                    repetition,
                    pairKey,
                    cancellationToken);
                await artifacts.SaveAsync(pair, cancellationToken);
                pairs.Add(pair);
                await artifacts.SaveReportAsync(
                    BuildReport(dataset, datasetHash, baselineCommit, pairs),
                    cancellationToken);
            }
        }

        var report = BuildReport(dataset, datasetHash, baselineCommit, pairs);
        await artifacts.SaveReportAsync(report, cancellationToken);
        return report;
    }

    private async Task<AdaptiveOfflinePairResult> ExecutePairAsync(
        LoadedAdaptiveOfflineDataset dataset,
        string datasetHash,
        string baselineCommit,
        AdaptiveOfflineTaskDefinition task,
        int repetition,
        string pairKey,
        CancellationToken cancellationToken)
    {
        AdaptiveOfflinePreparedPair prepared;
        try
        {
            prepared = await _preflight.PrepareAsync(
                dataset,
                task,
                _parser.ParseFromFile(dataset.ContractPath(task)),
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return FailedPair(dataset.Manifest.Id, datasetHash, task, repetition, pairKey,
                $"Preflight failed before provider execution: {ex.Message}");
        }

        var effectiveSeed = checked(task.Seed + repetition - 1);
        var fixedRequest = Request(
            AdaptiveOfflineArm.Fixed,
            dataset,
            datasetHash,
            baselineCommit,
            prepared,
            repetition,
            effectiveSeed,
            pairKey);
        var recommendedRequest = Request(
            AdaptiveOfflineArm.Recommended,
            dataset,
            datasetHash,
            baselineCommit,
            prepared,
            repetition,
            effectiveSeed,
            pairKey);
        var fixedResult = await ExecuteArmAsync(fixedRequest, cancellationToken);
        var recommendedResult = await ExecuteArmAsync(recommendedRequest, cancellationToken);
        var failures = new[] { fixedResult, recommendedResult }
            .Where(result => result.Status == AdaptiveOfflineResultStatus.Failed)
            .Select(result => $"{result.Arm}: {result.Failure}")
            .ToList();

        return new AdaptiveOfflinePairResult
        {
            PairKey = pairKey,
            Status = failures.Count == 0
                ? AdaptiveOfflineResultStatus.Completed
                : AdaptiveOfflineResultStatus.Failed,
            Failure = string.Join("; ", failures),
            DatasetId = dataset.Manifest.Id,
            DatasetHash = datasetHash,
            TaskId = task.Id,
            Repetition = repetition,
            EffectiveSeed = effectiveSeed,
            RecommendationEvidenceId = prepared.RecommendationEvidenceId,
            RecommendationEvidenceHash = prepared.RecommendationEvidenceHash,
            SourceEvidenceIds = prepared.SourceEvidenceIds,
            Fixed = fixedResult,
            Recommended = recommendedResult
        };
    }

    private async Task<AdaptiveOfflineArmResult> ExecuteArmAsync(
        AdaptiveOfflineArmExecutionRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            var execution = await _executor.ExecuteAsync(request, cancellationToken);
            return MapAndValidate(request, execution);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new AdaptiveOfflineArmResult
            {
                Arm = request.Arm,
                Status = AdaptiveOfflineResultStatus.Failed,
                Failure = $"{ex.GetType().Name}: {ex.Message}",
                Model = request.Plan.Model,
                ContextStrategy = request.ContextStrategy,
                Budget = request.Plan.Budget
            };
        }
    }

    private static AdaptiveOfflineArmResult MapAndValidate(
        AdaptiveOfflineArmExecutionRequest request,
        StagedExecutionResult execution)
    {
        var failures = new List<string>();
        if (!execution.Contract.Id.Equals(request.Task.Id, StringComparison.Ordinal))
            failures.Add("execution returned a different task");
        if (!execution.Baseline.Commit.Equals(
                request.BaselineCommit,
                StringComparison.OrdinalIgnoreCase))
        {
            failures.Add("execution used a different baseline");
        }
        if (!execution.Model.Equals(request.Plan.Model, StringComparison.Ordinal))
            failures.Add("execution used a different model");
        var expectedAdapter = request.Provider.Kind == AdaptiveOfflineProvider.Local
            ? "OllamaAdapter"
            : "CloudAdapter";
        if (!execution.ContextManifest.Adapter.Equals(
                expectedAdapter,
                StringComparison.Ordinal))
        {
            failures.Add("execution used a different provider adapter");
        }
        try
        {
            if (AdaptiveOfflinePreflight.NormalizeContextStrategy(
                    execution.ContextManifest.Strategy) != request.ContextStrategy)
            {
                failures.Add("execution used a different context strategy");
            }
        }
        catch (InvalidOperationException)
        {
            failures.Add("execution reported an unsupported context strategy");
        }
        if (!Equivalent(execution.Contract.Budget, request.Plan.Budget) ||
            execution.BudgetUsage.WallClockLimitSeconds !=
                request.Plan.Budget.MaxDurationSeconds ||
            execution.BudgetUsage.MaximumAttempts != request.Plan.Budget.MaxRetries + 1)
        {
            failures.Add("execution did not enforce the arm budget");
        }
        if (!execution.OriginalRepositoryUnchanged)
            failures.Add("execution changed the original repository");
        if (execution.EvidenceId == Guid.Empty ||
            string.IsNullOrWhiteSpace(execution.EvidenceLocation))
        {
            failures.Add("execution did not persist authenticated evidence");
        }
        var scopeViolations = execution.VerificationResults.Count(result =>
            result.Verifier.Equals("Scope", StringComparison.OrdinalIgnoreCase) &&
            result.Status == VerificationStatus.Fail);
        var securityViolations = execution.VerificationResults.Count(result =>
            result.Verifier.Equals("SecurityScan", StringComparison.OrdinalIgnoreCase) &&
            result.Status == VerificationStatus.Fail);
        var verified = execution.Decision.Decision == TaskDecision.Verified &&
            execution.CandidateChangeSet.ChangedFiles.Count > 0 &&
            execution.OriginalRepositoryUnchanged &&
            execution.EvidenceId != Guid.Empty &&
            scopeViolations == 0 && securityViolations == 0;
        var accounting = execution.AgentResult.UsageAccounting;

        return new AdaptiveOfflineArmResult
        {
            Arm = request.Arm,
            Status = failures.Count == 0
                ? AdaptiveOfflineResultStatus.Completed
                : AdaptiveOfflineResultStatus.Failed,
            Failure = string.Join("; ", failures),
            Adapter = execution.ContextManifest.Adapter,
            Model = execution.Model,
            ContextStrategy = request.ContextStrategy,
            Budget = request.Plan.Budget,
            Decision = execution.Decision.Decision,
            MatchesExpected = execution.Decision.Decision == request.Task.ExpectedDecision,
            VerifiedCodeChange = verified,
            FirstPassVerified = verified && execution.AgentRun.RetryCount == 0,
            InputTokens = execution.AgentResult.InputTokens,
            OutputTokens = execution.AgentResult.OutputTokens,
            EstimatedCostUsd = execution.AgentResult.EstimatedCost,
            EffectiveCostUsd = accounting?.AccountedCostUsd,
            CostBasis = accounting?.AccountedCostBasis ?? "unavailable",
            DurationSeconds = execution.BudgetUsage.WallClockElapsed.TotalSeconds,
            RetryCount = execution.AgentRun.RetryCount,
            ScopeViolationCount = scopeViolations,
            SecurityViolationCount = securityViolations,
            EvidenceId = execution.EvidenceId == Guid.Empty ? null : execution.EvidenceId,
            EvidenceLocation = execution.EvidenceLocation,
            OriginalRepositoryUnchanged = execution.OriginalRepositoryUnchanged
        };
    }

    private static AdaptiveOfflineArmExecutionRequest Request(
        AdaptiveOfflineArm arm,
        LoadedAdaptiveOfflineDataset dataset,
        string datasetHash,
        string baselineCommit,
        AdaptiveOfflinePreparedPair prepared,
        int repetition,
        int effectiveSeed,
        string pairKey)
    {
        var fixedArm = arm == AdaptiveOfflineArm.Fixed;
        return new AdaptiveOfflineArmExecutionRequest
        {
            Arm = arm,
            DatasetId = dataset.Manifest.Id,
            DatasetHash = datasetHash,
            PairKey = pairKey,
            RepositoryPath = dataset.RepositoryPath,
            BaselineCommit = baselineCommit,
            Provider = dataset.Manifest.Provider,
            Task = prepared.Definition,
            Repetition = repetition,
            EffectiveSeed = effectiveSeed,
            Contract = fixedArm ? prepared.FixedContract : prepared.RecommendedContract,
            Plan = fixedArm ? prepared.FixedPlan : prepared.RecommendedPlan,
            ContextStrategy = fixedArm
                ? prepared.FixedContextStrategy
                : prepared.RecommendedContextStrategy
        };
    }

    private static AdaptiveOfflinePairResult FailedPair(
        string datasetId,
        string datasetHash,
        AdaptiveOfflineTaskDefinition task,
        int repetition,
        string pairKey,
        string failure) => new()
        {
            PairKey = pairKey,
            Status = AdaptiveOfflineResultStatus.Failed,
            Failure = failure,
            DatasetId = datasetId,
            DatasetHash = datasetHash,
            TaskId = task.Id,
            Repetition = repetition,
            EffectiveSeed = checked(task.Seed + repetition - 1),
            RecommendationEvidenceId = task.RecommendationEvidenceId,
            RecommendationEvidenceHash = task.RecommendationEvidenceHash,
            Fixed = new AdaptiveOfflineArmResult
            {
                Arm = AdaptiveOfflineArm.Fixed,
                Status = AdaptiveOfflineResultStatus.Failed,
                Failure = failure
            },
            Recommended = new AdaptiveOfflineArmResult
            {
                Arm = AdaptiveOfflineArm.Recommended,
                Status = AdaptiveOfflineResultStatus.Failed,
                Failure = failure
            }
        };

    private static AdaptiveOfflineReport BuildReport(
        LoadedAdaptiveOfflineDataset dataset,
        string datasetHash,
        string baselineCommit,
        List<AdaptiveOfflinePairResult> pairs) => new()
        {
            DatasetId = dataset.Manifest.Id,
            DatasetVersion = dataset.Manifest.Version,
            DatasetHash = datasetHash,
            BaselineCommit = baselineCommit,
            Pairs = pairs.OrderBy(pair => pair.TaskId, StringComparer.Ordinal)
                .ThenBy(pair => pair.Repetition)
                .ToList(),
            Analysis = AdaptiveOfflineAnalyzer.Analyze(dataset.Manifest, pairs)
        };

    private static bool Equivalent(ExecutionBudget left, ExecutionBudget right) =>
        left.MaxTokens == right.MaxTokens &&
        left.MaxCostUsd == right.MaxCostUsd &&
        left.MaxRetries == right.MaxRetries &&
        left.MaxDurationSeconds == right.MaxDurationSeconds &&
        left.MaxFilesChanged == right.MaxFilesChanged;
}

public static class AdaptiveOfflineAnalyzer
{
    public static AdaptiveOfflineAnalysis Analyze(
        AdaptiveOfflineDatasetManifest manifest,
        IReadOnlyCollection<AdaptiveOfflinePairResult> pairs)
    {
        AdaptiveOfflineDatasetContract.Validate(manifest);
        ArgumentNullException.ThrowIfNull(pairs);
        var planned = manifest.Tasks.Count * manifest.Repetitions;
        var completed = pairs.Where(pair =>
            pair.Status == AdaptiveOfflineResultStatus.Completed).ToList();
        var distinctTasks = completed.Select(pair => pair.TaskId)
            .Distinct(StringComparer.Ordinal).Count();
        var candidateFailureRate = Rate(
            pairs.Count(pair => pair.Recommended.Status == AdaptiveOfflineResultStatus.Failed),
            planned);
        var scopeRate = Rate(
            pairs.Count(pair => pair.Recommended.ScopeViolationCount > 0),
            planned);
        var securityRate = Rate(
            pairs.Count(pair => pair.Recommended.SecurityViolationCount > 0),
            planned);
        var referenceMetric = AggregateMetric(completed.Select(pair => pair.Fixed));
        var candidateMetric = AggregateMetric(completed.Select(pair => pair.Recommended));
        double? relative = referenceMetric is > 0 && candidateMetric is not null
            ? candidateMetric.Value / referenceMetric.Value - 1
            : null;
        var primaryDeltas = completed
            .Where(pair => pair.Fixed.EffectiveCostUsd is > 0 &&
                           pair.Recommended.EffectiveCostUsd is > 0)
            .Select(pair =>
                (pair.Recommended.VerifiedCodeChange ? 1d : 0d) /
                    (double)pair.Recommended.EffectiveCostUsd!.Value -
                (pair.Fixed.VerifiedCodeChange ? 1d : 0d) /
                    (double)pair.Fixed.EffectiveCostUsd!.Value)
            .ToList();
        var latencyRegression = MedianLatencyRegression(completed);
        var distribution = ExperimentAnalyzer.Distribution(primaryDeltas);
        var (conclusion, reason) = Conclude(
            manifest,
            pairs.Count,
            completed.Count,
            distinctTasks,
            candidateFailureRate,
            scopeRate,
            securityRate,
            latencyRegression,
            referenceMetric,
            candidateMetric,
            relative,
            distribution);

        return new AdaptiveOfflineAnalysis
        {
            HypothesisId = manifest.Protocol.HypothesisId,
            Conclusion = conclusion,
            ConclusionReason = reason,
            PlannedPairs = planned,
            ObservedPairs = pairs.Count,
            CompletedPairs = completed.Count,
            DistinctCompletedTasks = distinctTasks,
            CandidateFailureRate = candidateFailureRate,
            CandidateScopeViolationRate = scopeRate,
            CandidateSecurityViolationRate = securityRate,
            ReferenceVccPerEffectiveDollar = referenceMetric,
            CandidateVccPerEffectiveDollar = candidateMetric,
            RelativePrimaryMetricImprovement = relative,
            MedianLatencyRegressionRate = latencyRegression,
            PairedPrimaryMetricDelta = distribution,
            VerifiedCodeChangeDelta = ExperimentAnalyzer.Distribution(completed.Select(pair =>
                (pair.Recommended.VerifiedCodeChange ? 1d : 0d) -
                (pair.Fixed.VerifiedCodeChange ? 1d : 0d))),
            TokenDelta = ExperimentAnalyzer.Distribution(completed.Select(pair =>
                (double)(pair.Recommended.InputTokens + pair.Recommended.OutputTokens -
                    pair.Fixed.InputTokens - pair.Fixed.OutputTokens))),
            LatencyDeltaSeconds = ExperimentAnalyzer.Distribution(completed.Select(pair =>
                pair.Recommended.DurationSeconds - pair.Fixed.DurationSeconds))
        };
    }

    private static (HypothesisConclusion Conclusion, string Reason) Conclude(
        AdaptiveOfflineDatasetManifest manifest,
        int observedPairs,
        int completedPairs,
        int distinctTasks,
        double failureRate,
        double scopeRate,
        double securityRate,
        double? latencyRegression,
        double? referenceMetric,
        double? candidateMetric,
        double? relative,
        ExperimentMetricDistribution delta)
    {
        var protocol = manifest.Protocol;
        var death = protocol.DeathCriteria;
        var planned = manifest.Tasks.Count * manifest.Repetitions;
        if (observedPairs < planned || completedPairs < planned ||
            distinctTasks < protocol.MinimumDistinctTasks)
        {
            return (HypothesisConclusion.Adjust,
                $"Only {completedPairs}/{planned} pairs and {distinctTasks}/" +
                $"{protocol.MinimumDistinctTasks} distinct tasks completed; missing observations " +
                "remain in the preregistered denominator.");
        }
        if (failureRate > death.MaximumCandidateFailureRate)
        {
            return (HypothesisConclusion.Abandon,
                $"Candidate failure rate {failureRate:P2} exceeded " +
                $"{death.MaximumCandidateFailureRate:P2}.");
        }
        if (scopeRate > death.MaximumCandidateScopeViolationRate)
        {
            return (HypothesisConclusion.Abandon,
                $"Candidate scope-violation rate {scopeRate:P2} exceeded " +
                $"{death.MaximumCandidateScopeViolationRate:P2}.");
        }
        if (securityRate > death.MaximumCandidateSecurityViolationRate)
        {
            return (HypothesisConclusion.Abandon,
                $"Candidate security-violation rate {securityRate:P2} exceeded " +
                $"{death.MaximumCandidateSecurityViolationRate:P2}.");
        }
        if (latencyRegression is not null &&
            latencyRegression > death.MaximumMedianLatencyRegressionRate)
        {
            return (HypothesisConclusion.Abandon,
                $"Median latency regression {latencyRegression:P2} exceeded " +
                $"{death.MaximumMedianLatencyRegressionRate:P2}.");
        }
        if (referenceMetric is null || candidateMetric is null ||
            delta.Count < planned)
        {
            return (HypothesisConclusion.Adjust,
                "Effective cost is missing or non-positive in at least one preregistered arm; " +
                "the primary metric is unavailable.");
        }
        if (referenceMetric == 0)
        {
            if (protocol.ZeroReferencePolicy == ZeroReferencePolicy.Adjust)
            {
                return (HypothesisConclusion.Adjust,
                    "The fixed controller produced zero VCC with valid cost and the " +
                    "preregistered zero-reference policy requires adjustment.");
            }
            if (delta.Mean < death.MinimumAbsoluteImprovement)
            {
                return (HypothesisConclusion.Abandon,
                    "Absolute paired improvement did not reach the preregistered minimum.");
            }
            return delta.MeanConfidenceIntervalLower > death.MinimumAbsoluteImprovement
                ? (HypothesisConclusion.Maintain,
                    "Absolute paired improvement and its 95% lower confidence bound exceeded " +
                    "the preregistered minimum.")
                : (HypothesisConclusion.Adjust,
                    "Absolute paired improvement reached the minimum, but uncertainty remains.");
        }
        if (relative < death.MinimumRelativeImprovement)
        {
            return (HypothesisConclusion.Abandon,
                "Relative primary-metric improvement did not reach the preregistered minimum.");
        }
        return delta.MeanConfidenceIntervalLower > 0
            ? (HypothesisConclusion.Maintain,
                "The candidate reached the preregistered effect and the paired 95% lower " +
                "confidence bound is above zero.")
            : (HypothesisConclusion.Adjust,
                "The point estimate reached the minimum, but paired uncertainty remains.");
    }

    private static double? AggregateMetric(IEnumerable<AdaptiveOfflineArmResult> arms)
    {
        var values = arms.ToList();
        if (values.Count == 0 || values.Any(arm => arm.EffectiveCostUsd is null or <= 0))
            return null;
        return values.Count(arm => arm.VerifiedCodeChange) /
            (double)values.Sum(arm => arm.EffectiveCostUsd!.Value);
    }

    private static double? MedianLatencyRegression(
        IReadOnlyCollection<AdaptiveOfflinePairResult> pairs)
    {
        if (pairs.Count == 0 || pairs.Any(pair => pair.Fixed.DurationSeconds <= 0))
            return null;
        var fixedMedian = Median(pairs.Select(pair => pair.Fixed.DurationSeconds));
        var candidateMedian = Median(pairs.Select(pair => pair.Recommended.DurationSeconds));
        return fixedMedian <= 0 ? null : candidateMedian / fixedMedian - 1;
    }

    private static double Median(IEnumerable<double> source)
    {
        var values = source.OrderBy(value => value).ToArray();
        var middle = values.Length / 2;
        return values.Length % 2 == 0
            ? (values[middle - 1] + values[middle]) / 2
            : values[middle];
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : numerator / (double)denominator;
}
