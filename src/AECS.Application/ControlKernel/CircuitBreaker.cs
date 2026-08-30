using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.ControlKernel;

public class CircuitBreakerResult
{
    public bool Tripped { get; init; }
    public string Reason { get; init; } = string.Empty;
    public string LimitType { get; init; } = string.Empty;
    public EvidenceEvent? Evidence { get; init; }
}

public class CircuitBreaker
{
    private readonly BudgetEnforcer _budgetEnforcer = new();
    private readonly ScopeEnforcer _scopeEnforcer = new();

    public CircuitBreakerResult Check(
        TaskContract contract,
        AgentRunResult agentResult,
        CandidateChangeSet candidateChangeSet,
        int retryCount)
    {
        // Check budget
        var budgetViolation = _budgetEnforcer.Check(
            contract.Budget,
            agentResult,
            retryCount,
            candidateChangeSet.ChangedFiles.Count);
        if (budgetViolation is not null)
        {
            return new CircuitBreakerResult
            {
                Tripped = true,
                Reason = budgetViolation.Message,
                LimitType = budgetViolation.LimitType,
                Evidence = new EvidenceEvent
                {
                    TaskId = contract.Id,
                    EventType = "BudgetExceeded",
                    Payload = System.Text.Json.JsonSerializer.Serialize(budgetViolation),
                    Authority = "deterministic"
                }
            };
        }

        // Check scope
        var scopeViolations = _scopeEnforcer.Check(contract.Scope, candidateChangeSet.ChangedFiles);
        if (scopeViolations.Count > 0)
        {
            var firstViolation = scopeViolations[0];
            return new CircuitBreakerResult
            {
                Tripped = true,
                Reason = firstViolation.Message,
                LimitType = "ScopeViolation",
                Evidence = new EvidenceEvent
                {
                    TaskId = contract.Id,
                    EventType = "ScopeViolation",
                    Payload = System.Text.Json.JsonSerializer.Serialize(scopeViolations),
                    Authority = "deterministic"
                }
            };
        }

        return new CircuitBreakerResult { Tripped = false };
    }
}
