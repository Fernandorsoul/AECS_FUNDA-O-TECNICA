namespace AECS.Domain.Models;

using System.Text.Json.Serialization;

public enum AgentFailureKind
{
    None = 0,
    Transient = 1,
    RateLimited = 2,
    Timeout = 3,
    Cancelled = 4,
    Permanent = 5,
    PolicyViolation = 6,
    BudgetExceeded = 7
}

public sealed class AgentAttemptEvidence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public int AttemptNumber { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public bool Success { get; init; }
    public int ExitCode { get; init; }
    public string ExitReason { get; init; } = string.Empty;
    public AgentFailureKind FailureKind { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentUsageAccounting? UsageAccounting { get; init; }
    public TimeSpan Duration { get; init; }
    public TimeSpan? RetryAfter { get; init; }
    public TimeSpan? RetryDelay { get; init; }
    public bool WillRetry { get; init; }
    public string DecisionReason { get; init; } = string.Empty;
}

public sealed class ExecutionBudgetEvidence
{
    public DateTime StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public TimeSpan WallClockElapsed { get; init; }
    public int WallClockLimitSeconds { get; init; }
    public int MaximumAttempts { get; init; }
    public int AttemptsUsed { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    public bool Exhausted { get; init; }
    public string ExhaustionReason { get; init; } = string.Empty;
}
