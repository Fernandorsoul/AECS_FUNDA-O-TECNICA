using AECS.Application.SemanticLinter;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class EB002Verifier : IVerifier
{
    private readonly EB002PatternVerifier _verifier = new();

    public string Name => "EB002-Pattern";
    public VerificationCategory Category => VerificationCategory.Probabilistic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var result = _verifier.Verify(context.RepoPath);

            if (result.HasViolations)
            {
                var violationMessages = result.Violations
                    .Take(5)
                    .Select(v => v.ViolationDetail);

                // EB002 is probabilistic — warnings don't block, errors do
                var hasErrors = result.Violations.Any(v => v.Severity >= RuleSeverity.Error);

                return Task.FromResult(new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = hasErrors ? VerificationStatus.Fail : VerificationStatus.Pass,
                    Severity = hasErrors ? Severity.Error : Severity.Warning,
                    Message = $"Pattern violations found ({result.Violations.Count}):\n{string.Join("\n", violationMessages)}"
                });
            }

            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No pattern violations found ({result.FilesScanned} files, {result.RulesChecked} rules)"
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
                Message = $"EB002 verifier error: {ex.Message}"
            });
        }
    }
}
