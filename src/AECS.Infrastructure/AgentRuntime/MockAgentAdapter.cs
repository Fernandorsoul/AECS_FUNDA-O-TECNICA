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

        // Default: simulate a successful execution
        return Task.FromResult(new AgentRunResult
        {
            Success = true,
            StdOut = $"Mock execution for: {request.Objective}",
            StdErr = string.Empty,
            ExitCode = 0,
            Duration = TimeSpan.FromSeconds(5),
            InputTokens = 500,
            OutputTokens = 300,
            EstimatedCost = 0m,
            FilesChanged = ["src/Customers/CustomerMapper.cs"],
            ExitReason = "Completed"
        });
    }
}
