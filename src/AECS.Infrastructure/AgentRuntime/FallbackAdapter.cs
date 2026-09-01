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

    public AgentContextProfile GetContextProfile(AgentExecutionRequest request)
    {
        var primary = _primary.GetContextProfile(request);
        var fallback = _fallback.GetContextProfile(request);
        return new AgentContextProfile
        {
            Adapter = $"{nameof(FallbackAdapter)}({primary.Adapter},{fallback.Adapter})",
            Model = $"{primary.Model}|{fallback.Model}",
            TokenizerId = primary.TokenizerId == fallback.TokenizerId
                ? primary.TokenizerId
                : ConservativeTokenCounter.Id,
            ContextWindowTokens = Math.Min(
                primary.ContextWindowTokens,
                fallback.ContextWindowTokens),
            ReservedOutputTokens = Math.Max(
                primary.ReservedOutputTokens,
                fallback.ReservedOutputTokens),
            PromptOverheadTokens = Math.Max(
                primary.PromptOverheadTokens,
                fallback.PromptOverheadTokens)
        };
    }

    public async Task<AgentRunResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        // Try primary (local) first
        var result = await _primary.ExecuteAsync(request, cancellationToken);

        if (!_shouldFallback(result) || IsHardStop(result, cancellationToken))
            return result;

        var remainingTokens = request.Budget.MaxTokens - result.InputTokens - result.OutputTokens;
        var remainingCost = request.Budget.MaxCostUsd - result.EstimatedCost;
        if (remainingTokens <= 0 || remainingCost < 0)
        {
            return Aggregate(
                result,
                new AgentRunResult
                {
                    Success = false,
                    StdErr = "Fallback was blocked because the agent budget is exhausted",
                    ExitCode = -1,
                    ExitReason = "BudgetExceeded",
                    FailureKind = AgentFailureKind.BudgetExceeded
                },
                AgentFailureKind.BudgetExceeded);
        }

        // Primary failed or produced no useful output — try fallback (cloud)
        Console.WriteLine($"  [AECS] Local model failed ({result.ExitReason}). Falling back to cloud API...");

        var fallbackResult = await _fallback.ExecuteAsync(
            WithBudget(request, remainingTokens, Math.Max(0m, remainingCost)),
            cancellationToken);

        if (!fallbackResult.Success)
            Console.WriteLine($"  [AECS] Cloud API also failed: {fallbackResult.StdErr}");
        else if (fallbackResult.FilesChanged.Count == 0)
            Console.WriteLine($"  [AECS] Cloud API returned no files.");
        else
            Console.WriteLine($"  [AECS] Cloud API succeeded: {fallbackResult.FilesChanged.Count} file(s).");

        var aggregate = Aggregate(result, fallbackResult, fallbackResult.FailureKind);
        if (aggregate.InputTokens + aggregate.OutputTokens > request.Budget.MaxTokens ||
            aggregate.EstimatedCost > request.Budget.MaxCostUsd)
        {
            return new AgentRunResult
            {
                Success = false,
                StdOut = aggregate.StdOut,
                StdErr = "Fallback result exceeded the remaining token or cost budget",
                ExitCode = -1,
                Duration = aggregate.Duration,
                InputTokens = aggregate.InputTokens,
                OutputTokens = aggregate.OutputTokens,
                EstimatedCost = aggregate.EstimatedCost,
                FilesChanged = aggregate.FilesChanged,
                ExitReason = "BudgetExceeded",
                FailureKind = AgentFailureKind.BudgetExceeded
            };
        }

        return aggregate;
    }

    private static bool DefaultShouldFallback(AgentRunResult result)
    {
        // FilesChanged is agent telemetry. Only the filesystem-derived
        // CandidateChangeSet can determine whether useful changes exist.
        if (result.Success)
            return false;
        var kind = Classify(result);
        return kind is AgentFailureKind.Transient or
            AgentFailureKind.RateLimited or
            AgentFailureKind.Timeout;
    }

    private static bool IsHardStop(
        AgentRunResult result,
        CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
            return true;
        return Classify(result) is AgentFailureKind.Cancelled or
            AgentFailureKind.Permanent or
            AgentFailureKind.PolicyViolation or
            AgentFailureKind.BudgetExceeded;
    }

    private static AgentFailureKind Classify(AgentRunResult result)
    {
        if (result.Success)
            return AgentFailureKind.None;
        if (result.FailureKind != AgentFailureKind.None)
            return result.FailureKind;
        if (result.ExitCode == 429)
            return AgentFailureKind.RateLimited;
        if (result.ExitCode == 408 || result.ExitCode is >= 500 and <= 599)
            return AgentFailureKind.Transient;
        if (result.ExitReason.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return AgentFailureKind.Timeout;
        if (result.ExitReason.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return AgentFailureKind.Cancelled;
        if (result.ExitReason.Contains("connection", StringComparison.OrdinalIgnoreCase))
            return AgentFailureKind.Transient;
        return AgentFailureKind.Permanent;
    }

    private static AgentExecutionRequest WithBudget(
        AgentExecutionRequest request,
        int remainingTokens,
        decimal remainingCost) => new()
        {
            TaskId = request.TaskId,
            Objective = request.Objective,
            AcceptanceCriteria = request.AcceptanceCriteria,
            RepoPath = request.RepoPath,
            Scope = request.Scope,
            Budget = new ExecutionBudget
            {
                MaxTokens = remainingTokens,
                MaxCostUsd = remainingCost,
                MaxRetries = request.Budget.MaxRetries,
                MaxDurationSeconds = request.Budget.MaxDurationSeconds,
                MaxFilesChanged = request.Budget.MaxFilesChanged
            },
            Risk = request.Risk,
            Model = request.Model,
            CodeContext = request.CodeContext,
            ContextPrompt = request.ContextPrompt
        };

    private static AgentRunResult Aggregate(
        AgentRunResult primary,
        AgentRunResult fallback,
        AgentFailureKind failureKind) => new()
        {
            Success = fallback.Success,
            StdOut = fallback.StdOut,
            StdErr = fallback.StdErr,
            ExitCode = fallback.ExitCode,
            Duration = primary.Duration + fallback.Duration,
            InputTokens = primary.InputTokens + fallback.InputTokens,
            OutputTokens = primary.OutputTokens + fallback.OutputTokens,
            EstimatedCost = primary.EstimatedCost + fallback.EstimatedCost,
            FilesChanged = fallback.FilesChanged,
            ExitReason = fallback.Success ? "CompletedViaFallback" : fallback.ExitReason,
            FailureKind = fallback.Success ? AgentFailureKind.None : failureKind,
            RetryAfter = fallback.RetryAfter
        };
}
