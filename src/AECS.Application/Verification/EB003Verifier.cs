using System.Diagnostics;
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
            // Get git diff of the workspace
            var diff = await GetGitDiffAsync(context.RepoPath, cancellationToken);

            if (string.IsNullOrEmpty(diff))
            {
                return new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = VerificationStatus.Skip,
                    Severity = Severity.Info,
                    Message = "No diff available for breaking change analysis"
                };
            }

            var result = _verifier.Verify(context.RepoPath, diff);

            if (result.HasBreakingChanges)
            {
                var messages = result.BreakingChanges
                    .Take(5)
                    .Select(bc => bc.Detail);

                var hasCritical = result.BreakingChanges.Any(bc => bc.Severity >= RuleSeverity.Critical);

                return new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = Name,
                    Status = hasCritical ? VerificationStatus.Fail : VerificationStatus.Pass,
                    Severity = hasCritical ? Severity.Critical : Severity.Warning,
                    Message = $"Breaking changes detected ({result.BreakingChanges.Count}):\n{string.Join("\n", messages)}"
                };
            }

            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = VerificationStatus.Pass,
                Severity = Severity.Info,
                Message = $"No breaking changes detected ({result.FilesAnalyzed} files analyzed)"
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
                Message = $"EB003 verifier error: {ex.Message}"
            };
        }
    }

    private static async Task<string> GetGitDiffAsync(string repoPath, CancellationToken cancellationToken)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "git",
                Arguments = "diff HEAD",
                WorkingDirectory = repoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processInfo };
            process.Start();
            var output = await process.StandardOutput.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return process.ExitCode == 0 ? output : "";
        }
        catch
        {
            return "";
        }
    }
}
