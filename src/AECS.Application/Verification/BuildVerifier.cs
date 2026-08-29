using System.Diagnostics;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class BuildVerifier : IVerifier
{
    public string Name => "Build";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "build --no-restore",
                WorkingDirectory = context.RepoPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = new Process { StartInfo = processInfo };
            process.Start();
            var stderr = await process.StandardError.ReadToEndAsync(cancellationToken);
            await process.WaitForExitAsync(cancellationToken);

            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = process.ExitCode == 0 ? VerificationStatus.Pass : VerificationStatus.Fail,
                Severity = process.ExitCode == 0 ? Severity.Info : Severity.Error,
                Message = process.ExitCode == 0 ? "Build succeeded" : $"Build failed: {stderr}"
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
                Message = $"Build verifier error: {ex.Message}"
            };
        }
    }
}
