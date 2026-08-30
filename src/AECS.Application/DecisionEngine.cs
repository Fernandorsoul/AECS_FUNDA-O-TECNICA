using AECS.Application.Verification;
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

        foreach (var verifier in requiredVerifiers)
        {
            var matches = results
                .Where(result => string.Equals(
                    result.Verifier,
                    verifier,
                    StringComparison.OrdinalIgnoreCase))
                .ToList();

            if (matches.Count == 0)
            {
                failures.Add($"{verifier}: required verifier result is missing");
                continue;
            }

            if (matches.Count > 1)
            {
                failures.Add($"{verifier}: duplicate verifier results are ambiguous");
                continue;
            }

            var result = matches[0];
            if (result.Status != VerificationStatus.Pass)
                failures.Add($"{result.Verifier}: {result.Status} - {result.Message}");
        }

        if (contract.Verification.BlockCriticalSemanticFailures)
        {
            var criticalSemanticFailures = results.Where(result =>
                result.Verifier.StartsWith("EB", StringComparison.OrdinalIgnoreCase) &&
                result.Severity == Severity.Critical &&
                result.Status != VerificationStatus.Pass);
            foreach (var result in criticalSemanticFailures)
            {
                if (!requiredVerifiers.Contains(result.Verifier))
                {
                    failures.Add(
                        $"{result.Verifier}: critical semantic policy blocked {result.Status} - {result.Message}");
                }
            }
        }

        if (failures.Count > 0)
        {
            return new DecisionResult
            {
                Decision = TaskDecision.Rejected,
                TargetState = TaskState.Rejected,
                Reason = $"Required verifiers did not pass: {string.Join("; ", failures)}",
                Failures = failures
            };
        }

        if (contract.Approval.Production == ApprovalLevel.Human)
        {
            return new DecisionResult
            {
                Decision = TaskDecision.HumanReviewRequired,
                TargetState = TaskState.HumanReviewRequired,
                Reason = "All required verifiers passed but human approval is required"
            };
        }

        return new DecisionResult
        {
            Decision = TaskDecision.Verified,
            TargetState = TaskState.Verified,
            Reason = "All required verifiers are present and passed"
        };
    }

    public static IReadOnlySet<string> GetRequiredVerifiers(TaskContract contract)
    {
        var required = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "AgentSuccess",
            "Application",
            "NonEmptyChange",
            "Scope",
            "Budget"
        };

        if (contract.Verification.Build)
            required.Add("Build");

        if (contract.Verification.UnitTests || contract.Verification.IntegrationTests)
            required.Add("Tests");

        if (contract.Verification.Architecture)
            required.Add("EB001-Architecture");

        if (contract.Verification.SecurityScan)
            required.Add("SecurityScan");

        if (contract.AcceptanceCriteria.Count > 0 || contract.AcceptanceRequirements.Count > 0)
            required.Add(AcceptanceCriteriaVerifier.Name);

        foreach (var verifier in contract.Verification.RequiredSemanticVerifiers)
            required.Add(verifier);

        return required;
    }
}
