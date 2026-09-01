using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public static class AdaptiveShadowSchema
{
    public const string EvidenceVersion = "aecs.adaptive-shadow/v1";
    public const string StrategyVersion = "authenticated-risk-task-heuristic/v1";
}

public enum AdaptiveShadowDataStatus
{
    Ready,
    ColdStart,
    InsufficientSample,
    DriftDetected,
    ContradictoryHistory,
    InvalidHistory
}

public sealed class AdaptiveShadowPlan
{
    public string Model { get; init; } = string.Empty;
    public string ContextStrategy { get; init; } = string.Empty;
    public ExecutionBudget Budget { get; init; } = ExecutionBudget.Default;
    public VerificationProfile Verification { get; init; } = new();
    public ExecutionCapabilityPolicy Capabilities { get; init; } =
        ExecutionCapabilityPolicy.RestrictiveDefault();
}

public sealed class AdaptiveShadowInputs
{
    public RiskLevel Risk { get; init; }
    public string TaskType { get; init; } = string.Empty;
    public string ObjectiveFingerprint { get; init; } = string.Empty;
    public int AuthenticatedRecords { get; init; }
    public int EligibleRecords { get; init; }
    public int MatchingRecords { get; init; }
    public int ExcludedIncompleteRecords { get; init; }
    public int ExcludedNonReproducibleRecords { get; init; }
    public int InvalidOrTamperedRecords { get; init; }
    public double? OlderSuccessRate { get; init; }
    public double? RecentSuccessRate { get; init; }
}

public sealed class AdaptiveShadowRecommendation
{
    public string SchemaVersion { get; init; } = AdaptiveShadowSchema.EvidenceVersion;
    public string StrategyVersion { get; init; } = AdaptiveShadowSchema.StrategyVersion;
    public AdaptiveShadowDataStatus DataStatus { get; init; }
    public AdaptiveShadowInputs Inputs { get; init; } = new();
    public AdaptiveShadowPlan FixedPlan { get; init; } = new();
    public AdaptiveShadowPlan RecommendedPlan { get; init; } = new();
    public string Justification { get; init; } = string.Empty;
    public List<Guid> SourceEvidenceIds { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
}

public sealed class AdaptiveShadowEvaluation
{
    public TaskDecision FixedControllerDecision { get; init; }
    public TaskState FixedControllerState { get; init; }
    public bool FixedControllerSucceeded { get; init; }
    public string ExecutedModel { get; init; } = string.Empty;
    public string ExecutedContextStrategy { get; init; } = string.Empty;
    public ExecutionBudget ExecutedBudget { get; init; } = ExecutionBudget.Default;
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal? AccountedCostUsd { get; init; }
    public double DurationSeconds { get; init; }
    public bool RecommendationAgreedWithFixedModel { get; init; }
    public bool RecommendationAgreedWithFixedContext { get; init; }
    public bool FixedPlanPreserved { get; init; }
    public bool CounterfactualExecuted { get; init; }
}

public sealed class AdaptiveShadowEvidence
{
    public AdaptiveShadowRecommendation Recommendation { get; init; } = new();
    public AdaptiveShadowEvaluation Evaluation { get; init; } = new();
}

public sealed class AdaptiveShadowReportGroup
{
    public RiskLevel Risk { get; init; }
    public string TaskType { get; init; } = string.Empty;
    public int Executions { get; init; }
    public int ReadyRecommendations { get; init; }
    public int VerifiedExecutions { get; init; }
    public int ModelAgreements { get; init; }
    public decimal? AverageAccountedCostUsd { get; init; }
    public double AverageDurationSeconds { get; init; }
}

public sealed class AdaptiveShadowReport
{
    public string SchemaVersion { get; init; } = "aecs.adaptive-shadow-report/v1";
    public DateTime GeneratedAt { get; init; } = DateTime.UtcNow;
    public int AuthenticatedRecords { get; init; }
    public int IncludedShadowRecords { get; init; }
    public int ExcludedRecords { get; init; }
    public List<AdaptiveShadowReportGroup> Groups { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
}
