using AECS.Domain.Enums;

namespace AECS.Application.Jarvis;

public enum JarvisFactKind
{
    Persisted,
    Derived,
    Interpretation
}

public enum JarvisEvidenceStatus
{
    Complete,
    Partial
}

public sealed class DurableHistoryQuery
{
    public Guid? EvidenceId { get; init; }
    public string? TaskId { get; init; }
    public Guid? RunId { get; init; }
    public Guid? CandidateId { get; init; }
    public int Limit { get; init; } = 50;
}

public sealed class DurableExecutionSummary
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public JarvisEvidenceStatus Status { get; init; }
    public Guid EvidenceId { get; init; }
    public string EvidenceSchemaVersion { get; init; } = string.Empty;
    public string Authority { get; init; } = string.Empty;
    public string EvidenceHash { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public Guid RunId { get; init; }
    public Guid CandidateId { get; init; }
    public string BaselineCommit { get; init; } = string.Empty;
    public string DiffHash { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; }
    public string Model { get; init; } = string.Empty;
    public TaskDecision Decision { get; init; }
    public TaskState State { get; init; }
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class DurableHistoryResult
{
    public string SchemaVersion { get; init; } = "aecs.jarvis-history/v1";
    public List<DurableExecutionSummary> Items { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
}

public sealed class DurableGateFact
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public Guid Id { get; init; }
    public string Phase { get; init; } = string.Empty;
    public string Verifier { get; init; } = string.Empty;
    public VerificationStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; }
}

public sealed class DurableAcceptanceFact
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public string CriterionId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public VerificationStatus Status { get; init; }
    public string EvidenceType { get; init; } = string.Empty;
    public string EvidenceReference { get; init; } = string.Empty;
    public List<string> EvidenceReferences { get; init; } = [];
}

public sealed class DurableAttemptFact
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public Guid Id { get; init; }
    public int Number { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public bool Success { get; init; }
    public string FailureKind { get; init; } = string.Empty;
    public bool WillRetry { get; init; }
    public string Reason { get; init; } = string.Empty;
}

public sealed class DurableBudgetFact
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public int AttemptsUsed { get; init; }
    public int MaximumAttempts { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCostUsd { get; init; }
    public decimal? AccountedCostUsd { get; init; }
    public string AccountedCostBasis { get; init; } = string.Empty;
    public double WallClockSeconds { get; init; }
    public int WallClockLimitSeconds { get; init; }
    public bool Exhausted { get; init; }
    public string ExhaustionReason { get; init; } = string.Empty;
}

public sealed class DurableContextFileFact
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string IncludedSha256 { get; init; } = string.Empty;
    public int IncludedTokens { get; init; }
    public bool Truncated { get; init; }
    public int Rank { get; init; }
    public List<string> Reasons { get; init; } = [];
}

public sealed class DurableContextFact
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public JarvisEvidenceStatus Status { get; init; }
    public string Id { get; init; } = string.Empty;
    public string SchemaVersion { get; init; } = string.Empty;
    public string StrategyVersion { get; init; } = string.Empty;
    public string Strategy { get; init; } = string.Empty;
    public string ManifestHash { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Tokenizer { get; init; } = string.Empty;
    public int EstimatedTokens { get; init; }
    public int MaximumTokens { get; init; }
    public List<DurableContextFileFact> Files { get; init; } = [];
}

public sealed class DurablePromotionFact
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public Guid Id { get; init; }
    public string Action { get; init; } = string.Empty;
    public string Status { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public string ApprovalReference { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
}

public sealed class DurablePersistedFacts
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Persisted;
    public string DecisionReason { get; init; } = string.Empty;
    public DateTime DecidedAt { get; init; }
    public List<string> RequiredVerifiers { get; init; } = [];
    public List<DurableGateFact> Gates { get; init; } = [];
    public List<DurableAcceptanceFact> AcceptanceCriteria { get; init; } = [];
    public List<DurableAttemptFact> Attempts { get; init; } = [];
    public DurableBudgetFact Budget { get; init; } = new();
    public DurableContextFact Context { get; init; } = new();
    public List<DurablePromotionFact> Promotions { get; init; } = [];
}

public sealed class DurableDerivedFacts
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Derived;
    public int TotalTokens { get; init; }
    public int ChangedFiles { get; init; }
    public int PassedGates { get; init; }
    public int FailedGates { get; init; }
    public int MissingRequiredGates { get; init; }
}

public sealed class DurableInterpretation
{
    public JarvisFactKind Origin { get; init; } = JarvisFactKind.Interpretation;
    public JarvisEvidenceStatus Status { get; init; }
    public string Summary { get; init; } = string.Empty;
}

public sealed class DurableExecutionExplanation
{
    public string SchemaVersion { get; init; } = "aecs.jarvis-explanation/v1";
    public DurableExecutionSummary Execution { get; init; } = new();
    public DurablePersistedFacts Persisted { get; init; } = new();
    public DurableDerivedFacts Derived { get; init; } = new();
    public DurableInterpretation Interpretation { get; init; } = new();
    public List<string> Diagnostics { get; init; } = [];
}

public sealed class DurableExplanationResult
{
    public string SchemaVersion { get; init; } = "aecs.jarvis-explanation-result/v1";
    public DurableExecutionExplanation? Item { get; init; }
    public List<string> Diagnostics { get; init; } = [];
}

public sealed class DurableContextResult
{
    public string SchemaVersion { get; init; } = "aecs.jarvis-context/v1";
    public DurableExecutionSummary? Execution { get; init; }
    public DurableContextFact? Context { get; init; }
    public List<string> Diagnostics { get; init; } = [];
}
