using System.Text.Json;
using AECS.Application.ContextCompiler;
using AECS.Application.ControlKernel;
using AECS.Application.EvidenceGraph;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.AdaptiveController;

public sealed class AdaptiveOfflinePreparedPair
{
    public AdaptiveOfflineTaskDefinition Definition { get; init; } = new();
    public TaskContract FixedContract { get; init; } = new();
    public TaskContract RecommendedContract { get; init; } = new();
    public AdaptiveShadowPlan FixedPlan { get; init; } = new();
    public AdaptiveShadowPlan RecommendedPlan { get; init; } = new();
    public string FixedContextStrategy { get; init; } = string.Empty;
    public string RecommendedContextStrategy { get; init; } = string.Empty;
    public Guid RecommendationEvidenceId { get; init; }
    public string RecommendationEvidenceHash { get; init; } = string.Empty;
    public List<Guid> SourceEvidenceIds { get; init; } = [];
}

public sealed class AdaptiveOfflinePreflight
{
    private const int MinimumAuthenticatedSources = 5;
    private readonly IExecutionEvidenceStore _evidenceStore;
    private readonly EvidenceGraphService _evidenceGraph;

    public AdaptiveOfflinePreflight(
        IExecutionEvidenceStore evidenceStore,
        IEvidenceGraphSource evidenceGraphSource)
    {
        ArgumentNullException.ThrowIfNull(evidenceStore);
        ArgumentNullException.ThrowIfNull(evidenceGraphSource);
        _evidenceStore = evidenceStore;
        _evidenceGraph = new EvidenceGraphService(evidenceGraphSource);
    }

    public async Task<AdaptiveOfflinePreparedPair> PrepareAsync(
        LoadedAdaptiveOfflineDataset dataset,
        AdaptiveOfflineTaskDefinition definition,
        TaskContract inputContract,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(dataset);
        ArgumentNullException.ThrowIfNull(definition);
        ArgumentNullException.ThrowIfNull(inputContract);
        AdaptiveOfflineDatasetContract.Validate(dataset.Manifest);
        if (!dataset.Manifest.Tasks.Contains(definition))
        {
            throw new InvalidOperationException(
                "Adaptive offline task is not part of the validated dataset.");
        }

        var contract = TaskContractIntegrity.Seal(inputContract);
        if (!contract.Id.Equals(definition.Id, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Adaptive offline task ID does not match its TaskContract.");
        }

        var repositoryPath = Path.GetFullPath(dataset.RepositoryPath);
        _evidenceStore.EnsureRepositoryIsolation(repositoryPath);
        var scope = new EvidenceReadScope
        {
            RepositoryPath = repositoryPath,
            Principal = "adaptive-offline-gate"
        };
        var recommendationGraph = await _evidenceGraph.ShowAsync(
            definition.RecommendationEvidenceId,
            scope,
            cancellationToken) ?? throw new InvalidOperationException(
                "Authenticated shadow recommendation evidence was not found.");
        if (!recommendationGraph.Summary.EvidenceHash.Equals(
                definition.RecommendationEvidenceHash,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Shadow recommendation evidence hash does not match the preregistered hash.");
        }

        var recommendationEvidence = await _evidenceStore.LoadAsync(
            definition.RecommendationEvidenceId,
            cancellationToken) ?? throw new InvalidOperationException(
                "Shadow recommendation evidence disappeared after authentication.");
        ValidateRecommendationEvidence(dataset, definition, contract, recommendationEvidence);

        var recommendation = recommendationEvidence.AdaptiveShadow!.Recommendation;
        foreach (var sourceId in recommendation.SourceEvidenceIds)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var sourceGraph = await _evidenceGraph.ShowAsync(
                sourceId,
                scope,
                cancellationToken) ?? throw new InvalidOperationException(
                    $"Adaptive source evidence '{sourceId:N}' was not authenticated.");
            var source = await _evidenceStore.LoadAsync(sourceId, cancellationToken) ??
                throw new InvalidOperationException(
                    $"Adaptive source evidence '{sourceId:N}' disappeared after authentication.");
            ValidateSourceEvidence(
                sourceId,
                sourceGraph.Summary.CreatedAt,
                source,
                definition,
                contract,
                recommendation.GeneratedAt);
        }

        var fixedPlan = recommendation.FixedPlan;
        var recommendedPlan = recommendation.RecommendedPlan;
        var fixedContext = NormalizeContextStrategy(
            recommendationEvidence.AdaptiveShadow.Evaluation.ExecutedContextStrategy);
        var recommendedContext = NormalizeContextStrategy(recommendedPlan.ContextStrategy);
        ValidatePlans(contract, fixedPlan, recommendedPlan, fixedContext, recommendedContext);

        return new AdaptiveOfflinePreparedPair
        {
            Definition = definition,
            FixedContract = WithBudget(contract, fixedPlan.Budget),
            RecommendedContract = WithBudget(contract, recommendedPlan.Budget),
            FixedPlan = CopyPlan(fixedPlan, fixedContext),
            RecommendedPlan = CopyPlan(recommendedPlan, recommendedContext),
            FixedContextStrategy = fixedContext,
            RecommendedContextStrategy = recommendedContext,
            RecommendationEvidenceId = recommendationEvidence.Id,
            RecommendationEvidenceHash = recommendationGraph.Summary.EvidenceHash,
            SourceEvidenceIds = recommendation.SourceEvidenceIds.ToList()
        };
    }

    private static void ValidateRecommendationEvidence(
        LoadedAdaptiveOfflineDataset dataset,
        AdaptiveOfflineTaskDefinition definition,
        TaskContract contract,
        ExecutionEvidence evidence)
    {
        var shadow = evidence.AdaptiveShadow;
        if (evidence.Id != definition.RecommendationEvidenceId ||
            shadow is null ||
            shadow.Recommendation.DataStatus != AdaptiveShadowDataStatus.Ready ||
            shadow.Recommendation.SchemaVersion != AdaptiveShadowSchema.EvidenceVersion ||
            shadow.Recommendation.StrategyVersion != AdaptiveShadowSchema.StrategyVersion ||
            shadow.Recommendation.Diagnostics.Count != 0 ||
            shadow.Recommendation.Inputs.InvalidOrTamperedRecords != 0 ||
            !shadow.Evaluation.FixedPlanPreserved ||
            shadow.Evaluation.CounterfactualExecuted ||
            evidence.CreatedAt > dataset.Manifest.PreregisteredAtUtc ||
            shadow.Recommendation.GeneratedAt > evidence.CreatedAt ||
            definition.HistoryCutoffUtc > shadow.Recommendation.GeneratedAt ||
            !evidence.Baseline.Commit.Equals(
                dataset.Manifest.Repository.Baseline,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Shadow recommendation evidence is not a preregistered, Ready, non-governing recommendation.");
        }

        var inputs = shadow.Recommendation.Inputs;
        if (inputs.Risk != contract.Constraints.SecurityRisk ||
            inputs.TaskType != AdaptiveTaskTypeClassifier.Classify(contract.Objective) ||
            inputs.ObjectiveFingerprint != AdaptiveTaskTypeClassifier.Fingerprint(contract.Objective) ||
            shadow.Recommendation.SourceEvidenceIds.Count < MinimumAuthenticatedSources ||
            shadow.Recommendation.SourceEvidenceIds.Count != inputs.MatchingRecords ||
            shadow.Recommendation.SourceEvidenceIds.Distinct().Count() !=
                shadow.Recommendation.SourceEvidenceIds.Count ||
            shadow.Recommendation.SourceEvidenceIds.Contains(evidence.Id))
        {
            throw new InvalidOperationException(
                "Shadow recommendation inputs or authenticated source IDs do not match the evaluated task.");
        }
    }

    private static void ValidateSourceEvidence(
        Guid expectedId,
        DateTime graphCreatedAt,
        ExecutionEvidence evidence,
        AdaptiveOfflineTaskDefinition definition,
        TaskContract targetContract,
        DateTime recommendationGeneratedAt)
    {
        TaskContractIntegrity.ValidateForEvidence(evidence.TaskContract);
        if (evidence.Id != expectedId ||
            evidence.CreatedAt >= definition.HistoryCutoffUtc ||
            graphCreatedAt >= definition.HistoryCutoffUtc ||
            evidence.CreatedAt > recommendationGeneratedAt ||
            !IsTerminal(evidence.FinalDecision.State) ||
            evidence.AgentRun.Id == Guid.Empty ||
            evidence.AgentRun.TaskId != evidence.TaskContract.Id ||
            string.IsNullOrWhiteSpace(evidence.AgentRun.Model) ||
            string.IsNullOrWhiteSpace(evidence.Baseline.Commit) ||
            !IsSha256(evidence.ContextManifest.ManifestHash) ||
            !IsSha256(evidence.CandidateChangeSet.DiffHash) ||
            evidence.TaskContract.Constraints.SecurityRisk !=
                targetContract.Constraints.SecurityRisk ||
            AdaptiveTaskTypeClassifier.Classify(evidence.TaskContract.Objective) !=
                AdaptiveTaskTypeClassifier.Classify(targetContract.Objective))
        {
            throw new InvalidOperationException(
                $"Adaptive source evidence '{expectedId:N}' violates the temporal or integrity preflight.");
        }
    }

    private static void ValidatePlans(
        TaskContract contract,
        AdaptiveShadowPlan fixedPlan,
        AdaptiveShadowPlan recommendedPlan,
        string fixedContext,
        string recommendedContext)
    {
        if (string.IsNullOrWhiteSpace(fixedPlan.Model) ||
            string.IsNullOrWhiteSpace(recommendedPlan.Model) ||
            !ContextStrategyIds.IsSupported(fixedContext) ||
            !ContextStrategyIds.IsSupported(recommendedContext) ||
            !Equivalent(contract.Budget, fixedPlan.Budget) ||
            !Equivalent(contract.Verification, fixedPlan.Verification) ||
            !Equivalent(fixedPlan.Verification, recommendedPlan.Verification) ||
            ExecutionCapabilityPolicyFingerprint.Create(contract.Execution.EffectiveCapabilities) !=
                ExecutionCapabilityPolicyFingerprint.Create(fixedPlan.Capabilities) ||
            ExecutionCapabilityPolicyFingerprint.Create(fixedPlan.Capabilities) !=
                ExecutionCapabilityPolicyFingerprint.Create(recommendedPlan.Capabilities))
        {
            throw new InvalidOperationException(
                "Adaptive offline plans do not preserve the authoritative contract guardrails.");
        }

        CapabilityPolicyGuard.EnsureNoExpansion(fixedPlan.Capabilities, recommendedPlan.Capabilities);
        if (!BoundedBy(recommendedPlan.Budget, fixedPlan.Budget))
        {
            throw new InvalidOperationException(
                "Adaptive offline recommendation expands the fixed execution budget.");
        }

        if (fixedPlan.Model.Equals(recommendedPlan.Model, StringComparison.Ordinal) &&
            fixedContext.Equals(recommendedContext, StringComparison.Ordinal) &&
            Equivalent(fixedPlan.Budget, recommendedPlan.Budget))
        {
            throw new InvalidOperationException(
                "Adaptive offline pair has no treatment difference to evaluate.");
        }
    }

    public static string NormalizeContextStrategy(string value)
    {
        if (value.Equals(ContextStrategyIds.GraphRanked, StringComparison.Ordinal) ||
            value.StartsWith(ContextStrategyIds.GraphRanked + "+", StringComparison.Ordinal))
        {
            return ContextStrategyIds.GraphRanked;
        }
        if (value.Equals(ContextStrategyIds.NaivePathOrder, StringComparison.Ordinal) ||
            value.StartsWith(ContextStrategyIds.NaivePathOrder + "+", StringComparison.Ordinal))
        {
            return ContextStrategyIds.NaivePathOrder;
        }
        throw new InvalidOperationException(
            $"Adaptive offline context strategy '{value}' is unsupported.");
    }

    private static TaskContract WithBudget(TaskContract source, ExecutionBudget budget) =>
        TaskContractIntegrity.Seal(new TaskContract
        {
            Id = source.Id,
            Objective = source.Objective,
            AcceptanceCriteria = source.AcceptanceCriteria,
            AcceptanceRequirements = source.AcceptanceRequirements,
            Scope = source.Scope,
            Constraints = source.Constraints,
            Budget = budget,
            Execution = source.Execution,
            Verification = source.Verification,
            Approval = source.Approval,
            Status = source.Status,
            CreatedAt = source.CreatedAt
        });

    private static AdaptiveShadowPlan CopyPlan(
        AdaptiveShadowPlan source,
        string contextStrategy) => new()
        {
            Model = source.Model,
            ContextStrategy = contextStrategy,
            Budget = source.Budget,
            Verification = source.Verification,
            Capabilities = source.Capabilities
        };

    private static bool BoundedBy(ExecutionBudget proposed, ExecutionBudget maximum) =>
        proposed.MaxTokens > 0 && proposed.MaxTokens <= maximum.MaxTokens &&
        proposed.MaxCostUsd > 0 && proposed.MaxCostUsd <= maximum.MaxCostUsd &&
        proposed.MaxRetries >= 0 && proposed.MaxRetries <= maximum.MaxRetries &&
        proposed.MaxDurationSeconds > 0 &&
        proposed.MaxDurationSeconds <= maximum.MaxDurationSeconds &&
        proposed.MaxFilesChanged > 0 && proposed.MaxFilesChanged <= maximum.MaxFilesChanged;

    private static bool Equivalent(ExecutionBudget left, ExecutionBudget right) =>
        left.MaxTokens == right.MaxTokens &&
        left.MaxCostUsd == right.MaxCostUsd &&
        left.MaxRetries == right.MaxRetries &&
        left.MaxDurationSeconds == right.MaxDurationSeconds &&
        left.MaxFilesChanged == right.MaxFilesChanged;

    private static bool Equivalent(VerificationProfile left, VerificationProfile right) =>
        JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private static bool IsSha256(string value) =>
        value is not null && value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => Uri.IsHexDigit(character) && !char.IsUpper(character));

    private static bool IsTerminal(TaskState state) => state is
        TaskState.Verified or TaskState.Rejected or TaskState.HumanReviewRequired or
        TaskState.BudgetExceeded or TaskState.ScopeViolation or TaskState.TimedOut or
        TaskState.AgentFailed or TaskState.Cancelled;
}
