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
            var input = SemanticAnalysisInput.From(context);
            var result = _verifier.Verify(input);

            if (result.HasViolations)
            {
                var violationMessages = result.Violations
                    .Take(5)
                    .Select(v => v.Justification);

                // EB002 is probabilistic — warnings don't block, errors do
                var hasErrors = result.Violations.Any(v => v.Severity >= RuleSeverity.Error);

                return Task.FromResult(new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = hasErrors ? VerificationStatus.Fail : VerificationStatus.Pass,
                    Severity = hasErrors ? Severity.Error : Severity.Warning,
                    Message = $"Pattern violations found ({result.Violations.Count}):\n{string.Join("\n", violationMessages)}",
                    Semantic = SemanticEvidenceFactory.Create(input, result.Violations)
                });
            }

            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No pattern violations found ({result.FilesScanned} impacted files, {result.RulesChecked} rules)",
                Semantic = SemanticEvidenceFactory.Create(input, [])
            });
        }
        catch (Exception ex)
        {
            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Error,
                Severity = Severity.Critical,
                Message = $"EB002 verifier error: {ex.Message}"
            });
        }
    }
}
