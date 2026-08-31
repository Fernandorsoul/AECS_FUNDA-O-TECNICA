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
            Arguments = request.Arguments
                .Select(argument => NormalizeWorkspaceArgument(context.RepoPath, argument))
                .ToList(),
            WorkingDirectory = relativeWorkingDirectory,
            ExitCode = result.ExitCode,
            TimedOut = result.TimedOut,
            Cancelled = result.Cancelled,
            Duration = result.Duration,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            Environment = result.Environment
        };
    }

    private static string NormalizeWorkspaceArgument(string workspacePath, string argument)
    {
        if (!Path.IsPathRooted(argument))
            return argument;

        try
        {
            var root = Path.GetFullPath(workspacePath)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            var candidate = Path.GetFullPath(argument);
            var comparison = OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;
            if (!candidate.StartsWith(root + Path.DirectorySeparatorChar, comparison))
                return argument;

            return Path.GetRelativePath(root, candidate).Replace('\\', '/');
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return argument;
        }
    }
}
