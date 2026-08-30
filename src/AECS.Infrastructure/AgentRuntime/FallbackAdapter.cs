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

        if (!fallbackResult.Success)
            Console.WriteLine($"  [AECS] Cloud API also failed: {fallbackResult.StdErr}");
        else if (fallbackResult.FilesChanged.Count == 0)
            Console.WriteLine($"  [AECS] Cloud API returned no files.");
        else
            Console.WriteLine($"  [AECS] Cloud API succeeded: {fallbackResult.FilesChanged.Count} file(s).");

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
        // FilesChanged is agent telemetry. Only the filesystem-derived
        // CandidateChangeSet can determine whether useful changes exist.
        return !result.Success;
    }
}
