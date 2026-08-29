using AECS.Application.ControlKernel;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class ScopeVerifier : IVerifier
{
    private readonly ScopeEnforcer _scopeEnforcer = new();

    public string Name => "Scope";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        var violations = _scopeEnforcer.Check(
            context.Contract.Scope,
            context.AgentResult.FilesChanged);

        if (violations.Count > 0)
        {
            var messages = violations.Select(v => v.Message);
            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Fail,
                Severity = Severity.Error,
                Message = $"Scope violations: {string.Join("; ", messages)}"
            });
        }

        return Task.FromResult(new VerificationResult
        {
            AgentRunId = context.AgentRunId,
            Verifier = Name,
            Status = VerificationStatus.Pass,
            Severity = Severity.Info,
            Message = "All files within allowed scope"
        });
    }
}
