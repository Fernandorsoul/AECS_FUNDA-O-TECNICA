using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application;

public class DecisionResult
{
    public TaskDecision Decision { get; init; }
    public TaskState TargetState { get; init; }
    public string Reason { get; init; } = string.Empty;
    public List<string> Failures { get; init; } = [];
}

public class DecisionEngine
{
    public DecisionResult Decide(
        IReadOnlyList<VerificationResult> results,
        TaskContract contract)
    {
        var requiredVerifiers = GetRequiredVerifiers(contract);
        var failures = new List<string>();

        // Regra 1: qualquer verificador obrigatório com Fail → REJECTED
        foreach (var result in results)
        {
            if (requiredVerifiers.Contains(result.Verifier) &&
                result.Status is VerificationStatus.Fail or VerificationStatus.Error)
            {
                failures.Add($"{result.Verifier}: {result.Message}");
            }
        }

        if (failures.Count > 0)
        {
            return new DecisionResult
            {
                Decision = TaskDecision.Rejected,
                TargetState = TaskState.Rejected,
                Reason = $"Required verifiers failed: {string.Join("; ", failures)}",
                Failures = failures
            };
        }

        // Regra 2: todos passam + approval=human → HUMAN_REVIEW_REQUIRED
        if (contract.Approval.Production == ApprovalLevel.Human)
        {
            return new DecisionResult
            {
                Decision = TaskDecision.HumanReviewRequired,
                TargetState = TaskState.HumanReviewRequired,
                Reason = "All verifiers passed but human approval required"
            };
        }

        // Regra 3: todos passam + approval=none → VERIFIED
        return new DecisionResult
        {
            Decision = TaskDecision.Verified,
            TargetState = TaskState.Verified,
            Reason = "All required verifiers passed"
        };
    }

    private static HashSet<string> GetRequiredVerifiers(TaskContract contract)
    {
        var required = new HashSet<string>();

        if (contract.Verification.Build)
            required.Add("Build");

        if (contract.Verification.UnitTests)
            required.Add("Tests");

        if (contract.Verification.Scope)
            required.Add("Scope");

        if (contract.Verification.Budget)
            required.Add("Budget");

        // EB001-EB005 são verificadores semânticos informativos, não gates
        // Eles podem ter falsos positivos e não devem bloquear automaticamente
        return required;
    }
}
