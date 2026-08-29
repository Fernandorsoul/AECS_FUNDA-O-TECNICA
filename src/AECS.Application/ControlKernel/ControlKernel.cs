using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application.ControlKernel;

public class KernelDecision
{
    public bool Allowed { get; init; }
    public TaskState TargetState { get; init; }
    public string Reason { get; init; } = string.Empty;
    public BudgetViolation? BudgetViolation { get; init; }
    public IReadOnlyList<ScopeViolation>? ScopeViolations { get; init; }
    public EvidenceEvent? Evidence { get; init; }
}

public class ControlKernel
{
    private readonly BudgetEnforcer _budgetEnforcer = new();
    private readonly ScopeEnforcer _scopeEnforcer = new();
    private readonly CircuitBreaker _circuitBreaker = new();

    public KernelDecision ValidateExecution(
        TaskContract contract,
        AgentRunResult agentResult,
        int retryCount = 0)
    {
        // Use circuit breaker for comprehensive check
        var breakerResult = _circuitBreaker.Check(contract, agentResult, retryCount);

        if (breakerResult.Tripped)
        {
            var targetState = breakerResult.LimitType switch
            {
                "ScopeViolation" => TaskState.ScopeViolation,
                "MaxDurationSeconds" => TaskState.TimedOut,
                _ => TaskState.BudgetExceeded
            };

            return new KernelDecision
            {
                Allowed = false,
                TargetState = targetState,
                Reason = breakerResult.Reason,
                Evidence = breakerResult.Evidence
            };
        }

        // Check if human review is required
        if (contract.Approval.Production == ApprovalLevel.Human)
        {
            return new KernelDecision
            {
                Allowed = true,
                TargetState = TaskState.HumanReviewRequired,
                Reason = "Human approval required by policy"
            };
        }

        return new KernelDecision
        {
            Allowed = true,
            TargetState = TaskState.Verifying,
            Reason = "All enforcement checks passed"
        };
    }
}
