namespace AECS.Domain.Enums;

public enum TaskState
{
    Created,
    ContractReady,
    Planned,
    Running,
    Verifying,
    Verified,
    Rejected,
    HumanReviewRequired,
    BudgetExceeded,
    ScopeViolation,
    TimedOut,
    AgentFailed,
    Cancelled
}
