using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.AgentRuntime;

public class FallbackAdapter : IAgentAdapter
{
    private readonly IAgentAdapter _primary;
    private readonly IAgentAdapter _fallback;
    private readonly Func<AgentRunResult, bool> _shouldFallback;

    public FallbackAdapter(
        IAgentAdapter primary,
        IAgentAdapter fallback,
        Func<AgentRunResult, bool>? shouldFallback = null)
    {
        _primary = primary;
        _fallback = fallback;
        _shouldFallback = shouldFallback ?? DefaultShouldFallback;
    }

    public async Task<AgentRunResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        // Try primary (local) first
        var result = await _primary.ExecuteAsync(request, cancellationToken);

        if (!_shouldFallback(result))
            return result;

        // Primary failed or produced no useful output — try fallback (cloud)
        Console.WriteLine($"  [AECS] Local model failed ({result.ExitReason}). Falling back to cloud API...");

        var fallbackResult = await _fallback.ExecuteAsync(request, cancellationToken);

        // Mark as fallback execution
        return new AgentRunResult
        {
            Success = fallbackResult.Success,
            StdOut = fallbackResult.StdOut,
            StdErr = fallbackResult.StdErr,
            ExitCode = fallbackResult.ExitCode,
            Duration = result.Duration + fallbackResult.Duration,
            InputTokens = result.InputTokens + fallbackResult.InputTokens,
            OutputTokens = result.OutputTokens + fallbackResult.OutputTokens,
            EstimatedCost = result.EstimatedCost + fallbackResult.EstimatedCost,
            FilesChanged = fallbackResult.FilesChanged,
            ExitReason = fallbackResult.Success ? "CompletedViaFallback" : fallbackResult.ExitReason
        };
    }

    private static bool DefaultShouldFallback(AgentRunResult result)
    {
        // Fallback if:
        // 1. Execution failed (connection error, timeout)
        // 2. No files were produced (model didn't understand the task)
        return !result.Success || result.FilesChanged.Count == 0;
    }
}
