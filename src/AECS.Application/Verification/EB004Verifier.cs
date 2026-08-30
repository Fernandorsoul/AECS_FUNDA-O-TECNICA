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
            var diff = context.CandidateChangeSet.Diff;

            if (string.IsNullOrEmpty(diff))
            {
                return new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = VerificationStatus.Skip,
                    Severity = Severity.Info,
                    Message = "No diff available for missing change analysis"
                };
            }

            var result = _verifier.Verify(context.RepoPath, diff);

            if (result.HasMissingChanges)
            {
                var messages = result.MissingChanges
                    .Take(5)
                    .Select(mc => mc.Detail);

                return new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = VerificationStatus.Pass, // Warnings don't block
                    Severity = Severity.Warning,
                    Message = $"Possible missing changes ({result.MissingChanges.Count}):\n{string.Join("\n", messages)}"
                };
            }

            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No missing changes detected ({result.FilesAnalyzed} files analyzed)"
            };
        }
        catch (Exception ex)
        {
            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Error,
                Severity = Severity.Warning,
                Message = $"EB004 verifier error: {ex.Message}"
            };
        }
    }

}
