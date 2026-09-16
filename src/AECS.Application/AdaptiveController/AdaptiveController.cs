using System.Security.Cryptography;
using System.Text;
using AECS.Application.ControlKernel;
using AECS.Application.EvidenceGraph;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.AdaptiveController;

public sealed class AdaptiveController : IAdaptiveShadowController
{
    private const int DefaultMinimumSamples = 5;
    private const int HistoryLimit = 500;
    private const string GovernedContext = "governed-by-context-compiler";
    private readonly IExecutionEvidenceStore _evidenceStore;
    private readonly EvidenceGraphService _evidenceGraph;
    private readonly int _minimumSamples;

    public AdaptiveController(
        IExecutionEvidenceStore evidenceStore,
        IEvidenceGraphSource evidenceGraphSource,
        int minimumSamples = DefaultMinimumSamples)
    {
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ArgumentNullException.ThrowIfNull(evidenceGraphSource);
        if (minimumSamples < 2)
            throw new ArgumentOutOfRangeException(nameof(minimumSamples));
        _evidenceStore = evidenceStore;
        _evidenceGraph = new EvidenceGraphService(evidenceGraphSource);
        _minimumSamples = minimumSamples;
    }

    public async Task<AdaptiveShadowRecommendation> RecommendAsync(
        string repositoryPath,
        TaskContract task,
        ExecutionPlan fixedPlan,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(fixedPlan);

        var history = await LoadAuthenticatedHistoryAsync(
            repositoryPath,
            HistoryLimit,
            cancellationToken);
        var taskType = AdaptiveTaskTypeClassifier.Classify(task.Objective);
        var eligible = new List<HistoricalFeature>();
        var incomplete = 0;
        var nonReproducible = 0;
        foreach (var evidence in history.Evidence)
        {
            var projection = Project(evidence);
            if (projection.Status == ProjectionStatus.Incomplete)
            {
                incomplete++;
                continue;
            }
            if (projection.Status == ProjectionStatus.NonReproducible)
            {
                nonReproducible++;
                continue;
            }
            eligible.Add(projection.Feature!);
        }

        var matching = eligible
            .Where(record => record.Risk == task.Constraints.SecurityRisk &&
                             record.TaskType == taskType)
            .OrderBy(record => record.CreatedAt)
            .ToList();
        var (olderRate, recentRate, drift) = DetectDrift(matching);
        var contradictory = HasContradictoryHistory(matching);
        var invalidCount = history.Diagnostics.Count;
        var status = SelectStatus(
            history.Evidence.Count,
            matching.Count,
            invalidCount,
            drift,
            contradictory);
        var recommendationPlan = status == AdaptiveShadowDataStatus.Ready
            ? SelectRecommendation(matching, fixedPlan)
            : Snapshot(fixedPlan, GovernedContext);
        EnsureRecommendationBounded(fixedPlan, recommendationPlan);

        return new AdaptiveShadowRecommendation
        {
            DataStatus = status,
            Inputs = new AdaptiveShadowInputs
            {
                Risk = task.Constraints.SecurityRisk,
                TaskType = taskType,
                ObjectiveFingerprint = AdaptiveTaskTypeClassifier.Fingerprint(task.Objective),
                AuthenticatedRecords = history.Evidence.Count,
                EligibleRecords = eligible.Count,
                MatchingRecords = matching.Count,
                ExcludedIncompleteRecords = incomplete,
                ExcludedNonReproducibleRecords = nonReproducible,
                InvalidOrTamperedRecords = invalidCount,
                OlderSuccessRate = olderRate,
                RecentSuccessRate = recentRate
            },
            FixedPlan = Snapshot(fixedPlan, GovernedContext),
            RecommendedPlan = recommendationPlan,
            Justification = Justification(status, matching.Count),
            SourceEvidenceIds = matching.Select(record => record.EvidenceId).ToList(),
            Diagnostics = history.Diagnostics
        };
    }

    public async Task<AdaptiveShadowReport> CreateReportAsync(
        string repositoryPath,
        int limit,
        CancellationToken cancellationToken)
    {
        if (limit is < 1 or > HistoryLimit)
            throw new ArgumentOutOfRangeException(nameof(limit), "Report limit must be 1-500.");
        var history = await LoadAuthenticatedHistoryAsync(
            repositoryPath,
            limit,
            cancellationToken);
        var included = history.Evidence
            .Where(evidence => evidence.AdaptiveShadow is not null &&
                               Project(evidence).Status == ProjectionStatus.Eligible)
            .ToList();
        var groups = included
            .GroupBy(evidence => new
            {
                evidence.AdaptiveShadow!.Recommendation.Inputs.Risk,
                evidence.AdaptiveShadow.Recommendation.Inputs.TaskType
            })
            .OrderBy(group => group.Key.Risk)
            .ThenBy(group => group.Key.TaskType, StringComparer.Ordinal)
            .Select(group =>
            {
                var evaluatedCosts = group
                    .Select(item => item.AdaptiveShadow!.Evaluation.AccountedCostUsd)
                    .Where(cost => cost.HasValue)
                    .Select(cost => cost!.Value)
                    .ToList();
                return new AdaptiveShadowReportGroup
                {
                    Risk = group.Key.Risk,
                    TaskType = group.Key.TaskType,
                    Executions = group.Count(),
                    ReadyRecommendations = group.Count(item =>
                        item.AdaptiveShadow!.Recommendation.DataStatus ==
                        AdaptiveShadowDataStatus.Ready),
                    VerifiedExecutions = group.Count(item =>
                        item.AdaptiveShadow!.Evaluation.FixedControllerDecision ==
                        TaskDecision.Verified),
                    ModelAgreements = group.Count(item =>
                        item.AdaptiveShadow!.Evaluation.RecommendationAgreedWithFixedModel),
                    AverageAccountedCostUsd = evaluatedCosts.Count == 0
                        ? null
                        : evaluatedCosts.Average(),
                    AverageDurationSeconds = group.Average(item =>
                        item.AdaptiveShadow!.Evaluation.DurationSeconds)
                };
            })
            .ToList();
        return new AdaptiveShadowReport
        {
            AuthenticatedRecords = history.Evidence.Count,
            IncludedShadowRecords = included.Count,
            ExcludedRecords = history.Evidence.Count - included.Count +
                              history.Diagnostics.Count,
            Groups = groups,
            Diagnostics = history.Diagnostics
        };
    }

    private async Task<AuthenticatedHistory> LoadAuthenticatedHistoryAsync(
        string repositoryPath,
        int limit,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(repositoryPath);
        _evidenceStore.EnsureRepositoryIsolation(fullPath);
        var query = await _evidenceGraph.ListAsync(
            new EvidenceGraphQuery { Limit = limit },
            new EvidenceReadScope
            {
                RepositoryPath = fullPath,
                Principal = "adaptive-shadow-controller"
            },
            cancellationToken);
        var evidence = new List<ExecutionEvidence>();
        var diagnostics = query.Diagnostics.ToList();
        foreach (var summary in query.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var loaded = await _evidenceStore.LoadAsync(
                    summary.EvidenceId,
                    cancellationToken);
                if (loaded is null)
                {
                    diagnostics.Add("An authenticated evidence record disappeared and was omitted.");
                    continue;
                }
                evidence.Add(loaded);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                diagnostics.Add($"Evidence record skipped: {ex.GetType().Name} - {ex.Message}");
            }
        }
        return new AuthenticatedHistory(
            evidence,
            diagnostics);
    }

    private AdaptiveShadowDataStatus SelectStatus(
        int authenticated,
        int matching,
        int invalid,
        bool drift,
        bool contradictory)
    {
        if (authenticated == 0 && invalid == 0)
            return AdaptiveShadowDataStatus.ColdStart;
        if (invalid > 0 && matching == 0)
            return AdaptiveShadowDataStatus.InvalidHistory;
        if (matching < _minimumSamples)
            return AdaptiveShadowDataStatus.InsufficientSample;
        if (drift)
            return AdaptiveShadowDataStatus.DriftDetected;
        if (contradictory)
            return AdaptiveShadowDataStatus.ContradictoryHistory;
        return AdaptiveShadowDataStatus.Ready;
    }

    private static AdaptiveShadowPlan SelectRecommendation(
        IReadOnlyList<HistoricalFeature> matching,
        ExecutionPlan fixedPlan)
    {
        var bestModel = matching
            .GroupBy(record => record.Model, StringComparer.Ordinal)
            .Where(group => group.Count() >= 2)
            .Select(group => new
            {
                Model = group.Key,
                SuccessRate = group.Count(record => record.Verified) / (double)group.Count(),
                AverageCost = group
                    .Where(record => record.AccountedCostUsd.HasValue)
                    .Select(record => record.AccountedCostUsd!.Value)
                    .DefaultIfEmpty(decimal.MaxValue)
                    .Average()
            })
            .OrderByDescending(model => model.SuccessRate)
            .ThenBy(model => model.AverageCost)
            .ThenBy(model => model.Model, StringComparer.Ordinal)
            .FirstOrDefault()?.Model ?? fixedPlan.Model;
        var successes = matching.Where(record => record.Verified).ToList();
        var recommendedBudget = successes.Count < 2
            ? fixedPlan.Budget
            : BoundedBudget(successes, fixedPlan.Budget);
        var recommendedContext = successes
            .Where(record => !string.IsNullOrWhiteSpace(record.ContextStrategy))
            .GroupBy(record => record.ContextStrategy, StringComparer.Ordinal)
            .OrderByDescending(group => group.Count())
            .ThenBy(group => group.Key, StringComparer.Ordinal)
            .FirstOrDefault()?.Key ?? GovernedContext;
        return new AdaptiveShadowPlan
        {
            Model = bestModel,
            ContextStrategy = recommendedContext,
            Budget = recommendedBudget,
            Verification = fixedPlan.Verification,
            Capabilities = fixedPlan.Capabilities
        };
    }

    private static ExecutionBudget BoundedBudget(
        IReadOnlyList<HistoricalFeature> successful,
        ExecutionBudget maximum)
    {
        var tokens = Percentile75(successful.Select(item => item.Tokens));
        var durations = Percentile75(successful.Select(item => item.DurationSeconds));
        var costs = successful
            .Where(item => item.AccountedCostUsd.HasValue)
            .Select(item => item.AccountedCostUsd!.Value)
            .OrderBy(item => item)
            .ToList();
        return new ExecutionBudget
        {
            MaxTokens = Bound((int)Math.Ceiling(tokens * 1.25), maximum.MaxTokens),
            MaxCostUsd = costs.Count == 0
                ? maximum.MaxCostUsd
                : Bound(costs[(int)Math.Floor((costs.Count - 1) * 0.75)] * 1.25m,
                    maximum.MaxCostUsd),
            MaxRetries = maximum.MaxRetries,
            MaxDurationSeconds = Bound(
                (int)Math.Ceiling(durations * 1.25),
                maximum.MaxDurationSeconds),
            MaxFilesChanged = maximum.MaxFilesChanged
        };
    }

    private static int Percentile75(IEnumerable<int> values)
    {
        var ordered = values.OrderBy(value => value).ToList();
        return ordered[(int)Math.Floor((ordered.Count - 1) * 0.75)];
    }

    private static int Bound(int proposed, int maximum) =>
        maximum <= 0 ? maximum : Math.Min(maximum, Math.Max(1, proposed));

    private static decimal Bound(decimal proposed, decimal maximum) =>
        maximum <= 0 ? maximum : Math.Min(maximum, Math.Max(0.000001m, proposed));

    private static (double? Older, double? Recent, bool Drift) DetectDrift(
        IReadOnlyList<HistoricalFeature> matching)
    {
        if (matching.Count < 6)
            return (null, null, false);
        var split = matching.Count / 2;
        var older = matching.Take(split).Count(record => record.Verified) / (double)split;
        var recentCount = matching.Count - split;
        var recent = matching.Skip(split).Count(record => record.Verified) /
                     (double)recentCount;
        return (older, recent, Math.Abs(older - recent) >= 0.50d);
    }

    private static bool HasContradictoryHistory(
        IReadOnlyList<HistoricalFeature> matching) => matching
        .GroupBy(record => new { record.ObjectiveFingerprint, record.Model })
        .Any(group => group.Any(record => record.Verified) &&
                      group.Any(record => !record.Verified));

    private static Projection Project(ExecutionEvidence evidence)
    {
        if (evidence.Id == Guid.Empty ||
            evidence.AgentRun.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(evidence.AgentRun.Model) ||
            string.IsNullOrWhiteSpace(evidence.TaskContract.Id) ||
            evidence.AgentRun.TaskId != evidence.TaskContract.Id ||
            !IsTerminal(evidence.FinalDecision.State))
        {
            return new Projection(ProjectionStatus.Incomplete, null);
        }
        if (string.IsNullOrWhiteSpace(evidence.Baseline.Commit) ||
            string.IsNullOrWhiteSpace(evidence.ContextManifest.ManifestHash) ||
            !evidence.ContextManifest.ManifestHash.StartsWith("sha256:", StringComparison.Ordinal) ||
            string.IsNullOrWhiteSpace(evidence.CandidateChangeSet.DiffHash) ||
            !evidence.CandidateChangeSet.DiffHash.StartsWith("sha256:", StringComparison.Ordinal))
        {
            return new Projection(ProjectionStatus.NonReproducible, null);
        }
        return new Projection(ProjectionStatus.Eligible, new HistoricalFeature(
            evidence.Id,
            evidence.TaskContract.Constraints.SecurityRisk,
            AdaptiveTaskTypeClassifier.Classify(evidence.TaskContract.Objective),
            AdaptiveTaskTypeClassifier.Fingerprint(evidence.TaskContract.Objective),
            evidence.AgentRun.Model,
            evidence.ContextManifest.Strategy,
            evidence.FinalDecision.Decision == TaskDecision.Verified,
            evidence.AgentRun.InputTokens + evidence.AgentRun.OutputTokens,
            Math.Max(0, (int)Math.Ceiling(evidence.AgentResult.Duration.TotalSeconds)),
            evidence.AgentResult.UsageAccounting?.AccountedCostUsd,
            evidence.CreatedAt));
    }

    private static bool IsTerminal(TaskState state) => state is
        TaskState.Verified or TaskState.Rejected or TaskState.HumanReviewRequired or
        TaskState.BudgetExceeded or TaskState.ScopeViolation or TaskState.TimedOut or
        TaskState.AgentFailed or TaskState.Cancelled;

    private static AdaptiveShadowPlan Snapshot(ExecutionPlan plan, string contextStrategy) =>
        new()
        {
            Model = plan.Model,
            ContextStrategy = contextStrategy,
            Budget = plan.Budget,
            Verification = plan.Verification,
            Capabilities = plan.Capabilities
        };

    private static void EnsureRecommendationBounded(
        ExecutionPlan fixedPlan,
        AdaptiveShadowPlan recommendation)
    {
        CapabilityPolicyGuard.EnsureNoExpansion(
            fixedPlan.Capabilities,
            recommendation.Capabilities);
        if (recommendation.Budget.MaxTokens > fixedPlan.Budget.MaxTokens ||
            recommendation.Budget.MaxCostUsd > fixedPlan.Budget.MaxCostUsd ||
            recommendation.Budget.MaxRetries > fixedPlan.Budget.MaxRetries ||
            recommendation.Budget.MaxDurationSeconds > fixedPlan.Budget.MaxDurationSeconds ||
            recommendation.Budget.MaxFilesChanged > fixedPlan.Budget.MaxFilesChanged)
        {
            throw new InvalidOperationException(
                "Adaptive shadow recommendation attempted to expand the fixed budget.");
        }
    }

    private static string Justification(AdaptiveShadowDataStatus status, int matching) =>
        status switch
        {
            AdaptiveShadowDataStatus.Ready =>
                $"Recommendation derived from {matching} authenticated, completed, reproducible executions with the same risk and task type.",
            AdaptiveShadowDataStatus.ColdStart =>
                "No authenticated execution history is available; the deterministic fixed plan is recommended.",
            AdaptiveShadowDataStatus.InsufficientSample =>
                $"Only {matching} matching records are available; the deterministic fixed plan is recommended.",
            AdaptiveShadowDataStatus.DriftDetected =>
                "Recent and older success rates differ by at least 0.50; the deterministic fixed plan is recommended.",
            AdaptiveShadowDataStatus.ContradictoryHistory =>
                "The same objective and model have contradictory outcomes; the deterministic fixed plan is recommended.",
            _ =>
                "History failed authentication or integrity checks; the deterministic fixed plan is recommended."
        };

    private enum ProjectionStatus { Eligible, Incomplete, NonReproducible }

    private sealed record Projection(ProjectionStatus Status, HistoricalFeature? Feature);

    private sealed record HistoricalFeature(
        Guid EvidenceId,
        RiskLevel Risk,
        string TaskType,
        string ObjectiveFingerprint,
        string Model,
        string ContextStrategy,
        bool Verified,
        int Tokens,
        int DurationSeconds,
        decimal? AccountedCostUsd,
        DateTime CreatedAt);

    private sealed record AuthenticatedHistory(
        List<ExecutionEvidence> Evidence,
        List<string> Diagnostics);
}

public static class AdaptiveTaskTypeClassifier
{
    private static readonly (string Type, string[] Terms)[] Rules =
    [
        ("security", ["security", "secure", "vulnerability", "auth", "permission", "secret"]),
        ("test", ["test", "spec", "coverage", "assert"]),
        ("migration", ["migration", "migrate", "schema", "database"]),
        ("bugfix", ["fix", "bug", "error", "failure", "null", "incorrect"]),
        ("refactor", ["refactor", "cleanup", "simplify", "rename"]),
        ("feature", ["add", "create", "implement", "feature", "support", "integrate"])
    ];

    public static string Classify(string objective)
    {
        var normalized = objective.Trim().ToLowerInvariant();
        foreach (var (type, terms) in Rules)
        {
            if (terms.Any(term => normalized.Contains(term, StringComparison.Ordinal)))
                return type;
        }
        return "other";
    }

    public static string Fingerprint(string objective) => "sha256:" +
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            objective.Trim().ToLowerInvariant()))).ToLowerInvariant();
}
