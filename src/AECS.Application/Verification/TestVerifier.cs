using System.Diagnostics;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class TestVerifier : IVerifier
{
    public string Name => "Tests";
    public VerificationCategory Category => VerificationCategory.HighConfidence;

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context, CancellationToken cancellationToken)
    {
        try
        {
            var processInfo = new ProcessStartInfo
            {
                FileName = "dotnet",
                Arguments = "test --no-build",
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
                Message = process.ExitCode == 0 ? "All tests passed" : $"Tests failed: {stderr}"
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
                Message = $"Test verifier error: {ex.Message}"
            };
        }
    }
}
