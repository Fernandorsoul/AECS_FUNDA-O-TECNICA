using AECS.Domain.Models;

namespace AECS.Application.Verification;

internal static class ExecutionCommandEvidenceFactory
{
    public static ExecutionCommandEvidence Create(
        VerificationContext context,
        ProcessExecutionRequest request,
        ProcessExecutionResult result)
    {
        var relativeWorkingDirectory = Path.GetRelativePath(
                Path.GetFullPath(context.RepoPath),
                Path.GetFullPath(request.WorkingDirectory))
            .Replace('\\', '/');

        return new ExecutionCommandEvidence
        {
            FileName = request.FileName,
            Arguments = request.Arguments.ToList(),
            WorkingDirectory = relativeWorkingDirectory,
            ExitCode = result.ExitCode,
            TimedOut = result.TimedOut,
            Cancelled = result.Cancelled,
            Duration = result.Duration,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError
        };
    }
}
