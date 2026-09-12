using AECS.Application.SemanticLinter;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class EB004Verifier : IVerifier
{
    private readonly EB004MissingChangeVerifier _verifier = new();

    public string Name => "EB004-MissingChange";
    public VerificationCategory Category => VerificationCategory.Probabilistic;

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var input = SemanticAnalysisInput.From(context);
            if (!input.CanAnalyze)
            {
                return new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = VerificationStatus.Skip,
                    Severity = Severity.Info,
                    Message = "C# symbol graph did not load; semantic analysis skipped."
                };
            }

            var result = _verifier.Verify(input);

            if (result.HasMissingChanges)
            {
                var messages = result.MissingChanges
                    .Take(5)
                    .Select(mc => mc.Justification);

                var hasErrors = result.MissingChanges.Any(change =>
                    change.Severity >= RuleSeverity.Error);

                return new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = hasErrors ? VerificationStatus.Fail : VerificationStatus.Pass,
                    Severity = hasErrors ? Severity.Error : Severity.Warning,
                    Message = $"Possible missing changes ({result.MissingChanges.Count}):\n{string.Join("\n", messages)}",
                    Semantic = SemanticEvidenceFactory.Create(input, result.MissingChanges)
                };
            }

            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No missing changes detected ({result.FilesAnalyzed} impacted files analyzed)",
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
                Message = $"EB004 verifier error: {ex.Message}"
            };
        }
    }

}
