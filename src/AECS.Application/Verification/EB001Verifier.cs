using AECS.Application.SemanticLinter;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class EB001Verifier : IVerifier
{
    private readonly EB001ArchitectureVerifier _verifier = new();

    public string Name => "EB001-Architecture";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var result = _verifier.Verify(context.RepoPath);

            if (result.HasViolations)
            {
                var violationMessages = result.Violations
                    .Take(5) // Limit to first 5 violations
                    .Select(v => v.ViolationDetail);

                return Task.FromResult(new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = VerificationStatus.Fail,
                    Severity = result.Violations.Any(v => v.Severity == RuleSeverity.Critical)
                        ? Severity.Critical
                        : Severity.Error,
                    Message = $"Architecture violations found ({result.Violations.Count}):\n{string.Join("\n", violationMessages)}"
                });
            }

            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No architecture violations found ({result.FilesScanned} files, {result.RulesChecked} rules)"
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Error,
                Severity = Severity.Warning,
                Message = $"EB001 verifier error: {ex.Message}"
            });
        }
    }
}
