using AECS.Application.SemanticLinter;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class EB003Verifier : IVerifier
{
    private readonly EB003BreakingChangeVerifier _verifier = new();

    public string Name => "EB003-BreakingChange";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var input = SemanticAnalysisInput.From(context);
            var result = _verifier.Verify(input);

            if (result.HasBreakingChanges)
            {
                var messages = result.BreakingChanges
                    .Take(5)
                    .Select(bc => bc.Justification);

                var hasCritical = result.BreakingChanges.Any(bc => bc.Severity >= RuleSeverity.Critical);

                return new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = hasCritical ? VerificationStatus.Fail : VerificationStatus.Pass,
                    Severity = hasCritical ? Severity.Critical : Severity.Warning,
                    Message = $"Breaking changes detected ({result.BreakingChanges.Count}):\n{string.Join("\n", messages)}",
                    Semantic = SemanticEvidenceFactory.Create(input, result.BreakingChanges)
                };
            }

            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No breaking changes detected ({result.FilesAnalyzed} impacted files analyzed)",
                Semantic = SemanticEvidenceFactory.Create(input, [])
            };
        }
        catch (Exception ex)
        {
            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Error,
                Severity = Severity.Critical,
                Message = $"EB003 verifier error: {ex.Message}"
            };
        }
    }

}
