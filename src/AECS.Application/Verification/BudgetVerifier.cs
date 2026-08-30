using AECS.Application.ControlKernel;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class BudgetVerifier : IVerifier
{
    private readonly BudgetEnforcer _budgetEnforcer = new();

    public string Name => "Budget";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        var violation = _budgetEnforcer.Check(
            context.Contract.Budget,
            context.AgentResult,
            retryCount: 0,
            context.CandidateChangeSet.ChangedFiles.Count);

        if (violation is not null)
        {
            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Fail,
                Severity = Severity.Error,
                Message = violation.Message
            });
        }

        return Task.FromResult(new VerificationResult
        {
            AgentRunId = context.AgentRunId,
            Verifier = Name,
            Status = VerificationStatus.Pass,
            Severity = Severity.Info,
            Message = "Budget respected"
        });
    }
}
