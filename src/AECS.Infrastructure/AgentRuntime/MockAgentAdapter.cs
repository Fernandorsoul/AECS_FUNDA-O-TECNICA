using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.AgentRuntime;

public class MockAgentAdapter : IAgentAdapter
{
    public AgentRunResult? PresetResult { get; set; }

    public Task<AgentRunResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        if (PresetResult is not null)
            return Task.FromResult(PresetResult);

        var candidatePath = CreateCandidatePath(request.Scope.Allowed.FirstOrDefault());

        return Task.FromResult(new AgentRunResult
        {
            Success = true,
            StdOut = $"FILE: {candidatePath}\n```text\n" +
                $"AECS mock candidate for task {request.TaskId}\n```\n",
            StdErr = string.Empty,
            ExitCode = 0,
            Duration = TimeSpan.FromSeconds(5),
            InputTokens = 500,
            OutputTokens = 300,
            EstimatedCost = 0m,
            FilesChanged = [candidatePath],
            ExitReason = "Completed"
        });
    }

    private static string CreateCandidatePath(string? allowedPattern)
    {
        if (string.IsNullOrWhiteSpace(allowedPattern))
            return "aecs-mock-candidate.txt";

        var normalized = allowedPattern.Replace('\\', '/');
        if (normalized.EndsWith("/**", StringComparison.Ordinal))
            return $"{normalized[..^3].TrimEnd('/')}/aecs-mock-candidate.txt";

        if (normalized.Contains('*'))
            return normalized.Replace("**", "aecs-mock-candidate.txt").Replace("*", "aecs-mock-candidate");

        return normalized;
    }
}
