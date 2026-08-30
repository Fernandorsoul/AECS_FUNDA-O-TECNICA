namespace AECS.Domain.Enums;

public enum TaskState
{
    Created = 0,
    ContractReady = 1,
    Planned = 2,
    Running = 3,
    Verifying = 4,
    Verified = 5,
    Rejected = 6,
    HumanReviewRequired = 7,
    BudgetExceeded = 8,
    ScopeViolation = 9,
    TimedOut = 10,
    AgentFailed = 11,
    Cancelled = 12,
    CandidateProduced = 13,
    BaselineVerifying = 14
}
