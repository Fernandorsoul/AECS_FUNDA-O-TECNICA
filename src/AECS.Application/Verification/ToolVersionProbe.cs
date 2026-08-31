using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

internal static class ToolVersionProbe
{
    private static readonly (string Tool, string[] Arguments)[] Probes =
    [
        ("git", ["--version"]),
        ("dotnet", ["--version"])
    ];

    public static async Task CaptureAsync(
        IProcessRunner processRunner,
        VerificationContext context,
        CancellationToken cancellationToken,
        Func<TimeSpan>? remainingDuration = null)
    {
        foreach (var (tool, arguments) in Probes)
        {
            var timeout = TimeSpan.FromSeconds(30);
            if (remainingDuration is not null && remainingDuration() < timeout)
                timeout = remainingDuration();
            if (timeout <= TimeSpan.Zero)
                return;

            var request = new ProcessExecutionRequest
            {
                FileName = tool,
                Arguments = arguments,
                WorkingDirectory = context.RepoPath,
                Timeout = timeout
            };
            var result = await processRunner.RunAsync(request, cancellationToken);
            context.CommandEvidence.Add(
                ExecutionCommandEvidenceFactory.Create(context, request, result));
        }
    }

    public static bool IsProbe(ExecutionCommandEvidence command) =>
        command.Arguments.Count == 1 &&
        string.Equals(command.Arguments[0], "--version", StringComparison.Ordinal) &&
        (string.Equals(command.FileName, "git", StringComparison.OrdinalIgnoreCase) ||
         string.Equals(command.FileName, "dotnet", StringComparison.OrdinalIgnoreCase));
}
