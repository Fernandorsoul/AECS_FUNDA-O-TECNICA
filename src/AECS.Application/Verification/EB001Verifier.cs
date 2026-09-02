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
            var input = SemanticAnalysisInput.From(context);
            var result = _verifier.Verify(input);

            if (result.HasViolations)
            {
                var violationMessages = result.Violations
                    .Take(5) // Limit to first 5 violations
                    .Select(v => v.Justification);

                return Task.FromResult(new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = VerificationStatus.Fail,
                    Severity = result.Violations.Any(v => v.Severity == RuleSeverity.Critical)
                        ? Severity.Critical
                        : Severity.Error,
                    Message = $"Architecture violations found ({result.Violations.Count}):\n{string.Join("\n", violationMessages)}",
                    Semantic = SemanticEvidenceFactory.Create(input, result.Violations)
                });
            }

            return Task.FromResult(new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No architecture violations found ({result.FilesScanned} impacted files, {result.RulesChecked} rules)",
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
                Message = $"EB001 verifier error: {ex.Message}"
            });
        }
    }
}
