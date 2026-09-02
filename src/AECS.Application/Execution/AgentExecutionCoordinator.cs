using System.Diagnostics;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Execution;

public sealed class AgentExecutionOutcome
{
    public AgentRunResult Result { get; init; } = new();
    public List<AgentAttemptEvidence> Attempts { get; init; } = [];
    public string BudgetExhaustionReason { get; init; } = string.Empty;
}

public sealed class AgentExecutionCoordinator
{
    private static readonly TimeSpan DefaultMaximumBackoff = TimeSpan.FromSeconds(30);
    private readonly IAgentAdapter _agentAdapter;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private readonly TimeSpan _maximumBackoff;

    public AgentExecutionCoordinator(
        IAgentAdapter agentAdapter,
        Func<TimeSpan, CancellationToken, Task>? delay = null,
        TimeSpan? maximumBackoff = null)
    {
        _agentAdapter = agentAdapter;
        _delay = delay ?? Task.Delay;
        _maximumBackoff = maximumBackoff ?? DefaultMaximumBackoff;
        if (_maximumBackoff < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(maximumBackoff));
    }

    public async Task<AgentExecutionOutcome> ExecuteAsync(
        AgentExecutionRequest request,
        ExecutionBudgetScope budgetScope)
    {
        var attempts = new List<AgentAttemptEvidence>();
        var totalInputTokens = 0;
        var totalOutputTokens = 0;
        var totalCost = 0m;
        var executionStopwatch = Stopwatch.StartNew();
        AgentRunResult lastResult = new();

        for (var attemptNumber = 1;
             attemptNumber <= budgetScope.MaximumAttempts;
             attemptNumber++)
        {
            if (totalInputTokens + totalOutputTokens >= request.Budget.MaxTokens)
            {
                const string reason = "Token budget exhausted before the next agent attempt";
                return Outcome(
                    CancelledResult(
                        lastResult,
                        AgentFailureKind.BudgetExceeded,
                        reason,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    reason);
            }

            if (budgetScope.CallerCancellationRequested || budgetScope.WallClockExhausted)
            {
                var terminalKind = budgetScope.CallerCancellationRequested
                    ? AgentFailureKind.Cancelled
                    : AgentFailureKind.BudgetExceeded;
                var reason = budgetScope.CallerCancellationRequested
                    ? "Execution cancelled before the next agent attempt"
                    : "Wall-clock budget exhausted before the next agent attempt";
                return Outcome(
                    CancelledResult(lastResult, terminalKind, reason, executionStopwatch.Elapsed,
                        totalInputTokens, totalOutputTokens, totalCost),
                    attempts,
                    terminalKind == AgentFailureKind.BudgetExceeded ? reason : string.Empty);
            }

            var remainingBudget = budgetScope.RemainingAgentBudget(
                totalInputTokens,
                totalOutputTokens,
                totalCost);
            var attemptRequest = WithBudget(request, remainingBudget);
            var startedAt = DateTime.UtcNow;
            AgentRunResult attemptResult;
            try
            {
                attemptResult = await _agentAdapter.ExecuteAsync(
                    attemptRequest,
                    budgetScope.Token);
            }
            catch (OperationCanceledException)
            {
                attemptResult = new AgentRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    ExitReason = "Cancelled",
                    FailureKind = budgetScope.CallerCancellationRequested
                        ? AgentFailureKind.Cancelled
                        : AgentFailureKind.Timeout,
                    StdErr = budgetScope.CallerCancellationRequested
                        ? "Agent execution was cancelled"
                        : "Agent execution exceeded the shared wall-clock budget"
                };
            }
            catch (Exception ex)
            {
                attemptResult = new AgentRunResult
                {
                    Success = false,
                    ExitCode = -1,
                    ExitReason = "UnhandledAdapterError",
                    FailureKind = AgentFailureKind.Permanent,
                    StdErr = $"Agent adapter threw an exception: {ex.Message}"
                };
            }

            var finishedAt = DateTime.UtcNow;
            totalInputTokens += Math.Max(0, attemptResult.InputTokens);
            totalOutputTokens += Math.Max(0, attemptResult.OutputTokens);
            totalCost += Math.Max(0m, attemptResult.EstimatedCost);
            var failureKind = ClassifyFailure(attemptResult);
            var budgetFailure = GetBudgetFailure(
                request.Budget,
                totalInputTokens,
                totalOutputTokens,
                totalCost);

            if (budgetFailure is not null)
            {
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    AgentFailureKind.BudgetExceeded,
                    willRetry: false,
                    retryDelay: null,
                    budgetFailure));
                return Outcome(
                    CancelledResult(
                        attemptResult,
                        AgentFailureKind.BudgetExceeded,
                        budgetFailure,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    budgetFailure);
            }

            if (attemptResult.Success)
            {
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    AgentFailureKind.None,
                    willRetry: false,
                    retryDelay: null,
                    "Agent attempt succeeded"));
                return Outcome(
                    Aggregate(
                        attemptResult,
                        true,
                        AgentFailureKind.None,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    string.Empty);
            }

            if (budgetScope.CallerCancellationRequested || budgetScope.WallClockExhausted)
            {
                var cancelledByCaller = budgetScope.CallerCancellationRequested;
                var terminalKind = cancelledByCaller
                    ? AgentFailureKind.Cancelled
                    : AgentFailureKind.BudgetExceeded;
                var reason = cancelledByCaller
                    ? "Caller cancellation is permanent and was not retried"
                    : "Shared wall-clock budget was exhausted";
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    terminalKind,
                    willRetry: false,
                    retryDelay: null,
                    reason));
                return Outcome(
                    CancelledResult(
                        attemptResult,
                        terminalKind,
                        reason,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    terminalKind == AgentFailureKind.BudgetExceeded ? reason : string.Empty);
            }

            if (!IsRetryable(failureKind))
            {
                var reason = $"{failureKind} failures are permanent and are not retried";
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    failureKind,
                    willRetry: false,
                    retryDelay: null,
                    reason));
                return Outcome(
                    Aggregate(
                        attemptResult,
                        false,
                        failureKind,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    string.Empty);
            }

            if (attemptNumber >= budgetScope.MaximumAttempts)
            {
                const string reason = "Retry budget exhausted; maximum attempts reached";
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    failureKind,
                    willRetry: false,
                    retryDelay: null,
                    reason));
                return Outcome(
                    Aggregate(
                        attemptResult,
                        false,
                        failureKind,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    reason);
            }

            var consumedTokens = totalInputTokens + totalOutputTokens;
            if (consumedTokens >= request.Budget.MaxTokens ||
                (request.Budget.MaxCostUsd > 0 && totalCost >= request.Budget.MaxCostUsd))
            {
                var reason = consumedTokens >= request.Budget.MaxTokens
                    ? "Token budget exhausted before another retry could start"
                    : "Cost budget exhausted before another retry could start";
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    AgentFailureKind.BudgetExceeded,
                    willRetry: false,
                    retryDelay: null,
                    reason));
                return Outcome(
                    CancelledResult(
                        attemptResult,
                        AgentFailureKind.BudgetExceeded,
                        reason,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    reason);
            }

            var retryDelay = GetRetryDelay(attemptNumber, attemptResult.RetryAfter);
            if (retryDelay >= budgetScope.RemainingDuration)
            {
                const string reason = "Wall-clock budget cannot accommodate the required retry backoff";
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    AgentFailureKind.BudgetExceeded,
                    willRetry: false,
                    retryDelay,
                    reason));
                return Outcome(
                    CancelledResult(
                        attemptResult,
                        AgentFailureKind.BudgetExceeded,
                        reason,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    reason);
            }

            try
            {
                await _delay(retryDelay, budgetScope.Token);
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    failureKind,
                    willRetry: true,
                    retryDelay,
                    failureKind == AgentFailureKind.RateLimited
                        ? "Rate limit is transient; retry scheduled after bounded Retry-After/backoff"
                        : $"{failureKind} failure is transient; retry scheduled after bounded backoff"));
            }
            catch (OperationCanceledException)
            {
                var cancelledByCaller = budgetScope.CallerCancellationRequested;
                var terminalKind = cancelledByCaller
                    ? AgentFailureKind.Cancelled
                    : AgentFailureKind.BudgetExceeded;
                var reason = cancelledByCaller
                    ? "Execution was cancelled during retry backoff"
                    : "Wall-clock budget expired during retry backoff";
                attempts.Add(Evidence(
                    attemptNumber,
                    startedAt,
                    finishedAt,
                    attemptResult,
                    terminalKind,
                    willRetry: false,
                    retryDelay,
                    reason));
                return Outcome(
                    CancelledResult(
                        attemptResult,
                        terminalKind,
                        reason,
                        executionStopwatch.Elapsed,
                        totalInputTokens,
                        totalOutputTokens,
                        totalCost),
                    attempts,
                    terminalKind == AgentFailureKind.BudgetExceeded ? reason : string.Empty);
            }

            lastResult = attemptResult;
        }

        throw new InvalidOperationException("Agent retry loop terminated without an outcome.");
    }

    public static AgentFailureKind ClassifyFailure(AgentRunResult result)
    {
        if (result.Success)
            return AgentFailureKind.None;
        if (result.FailureKind != AgentFailureKind.None)
            return result.FailureKind;
        if (result.ExitCode == 429)
            return AgentFailureKind.RateLimited;
        if (result.ExitCode == 408 || result.ExitCode is >= 500 and <= 599)
            return AgentFailureKind.Transient;
        if (result.ExitReason.Contains("timeout", StringComparison.OrdinalIgnoreCase) ||
            result.ExitReason.Contains("timedout", StringComparison.OrdinalIgnoreCase))
        {
            return AgentFailureKind.Timeout;
        }
        if (result.ExitReason.Contains("cancel", StringComparison.OrdinalIgnoreCase))
            return AgentFailureKind.Cancelled;
        if (result.ExitReason.Contains("connection", StringComparison.OrdinalIgnoreCase) ||
            result.ExitReason.Contains("transient", StringComparison.OrdinalIgnoreCase))
        {
            return AgentFailureKind.Transient;
        }
        if (result.ExitReason.Contains("policy", StringComparison.OrdinalIgnoreCase))
            return AgentFailureKind.PolicyViolation;
        return AgentFailureKind.Permanent;
    }

    private static bool IsRetryable(AgentFailureKind failureKind) =>
        failureKind is AgentFailureKind.Transient or
            AgentFailureKind.RateLimited or
            AgentFailureKind.Timeout;

    private TimeSpan GetRetryDelay(int attemptNumber, TimeSpan? retryAfter)
    {
        var exponent = Math.Min(attemptNumber - 1, 20);
        var exponential = TimeSpan.FromSeconds(Math.Pow(2, exponent));
        var requested = retryAfter.HasValue &&
                        retryAfter.Value > TimeSpan.Zero &&
                        retryAfter.Value > exponential
            ? retryAfter.Value
            : exponential;
        return requested <= _maximumBackoff ? requested : _maximumBackoff;
    }

    private static string? GetBudgetFailure(
        ExecutionBudget budget,
        int inputTokens,
        int outputTokens,
        decimal cost)
    {
        var tokens = inputTokens + outputTokens;
        if (tokens > budget.MaxTokens)
            return $"Token budget exhausted: {tokens} > {budget.MaxTokens}";
        if (cost > budget.MaxCostUsd)
            return $"Cost budget exhausted: ${cost:F4} > ${budget.MaxCostUsd:F4}";
        return null;
    }

    private static AgentExecutionRequest WithBudget(
        AgentExecutionRequest request,
        ExecutionBudget budget) => new()
        {
            TaskId = request.TaskId,
            Objective = request.Objective,
            AcceptanceCriteria = request.AcceptanceCriteria,
            RepoPath = request.RepoPath,
            Scope = request.Scope,
            Budget = budget,
            Risk = request.Risk,
            Model = request.Model,
            CodeContext = request.CodeContext,
            ContextPrompt = request.ContextPrompt
        };

    private static AgentAttemptEvidence Evidence(
        int attemptNumber,
        DateTime startedAt,
        DateTime finishedAt,
        AgentRunResult result,
        AgentFailureKind failureKind,
        bool willRetry,
        TimeSpan? retryDelay,
        string decisionReason) => new()
        {
            AttemptNumber = attemptNumber,
            StartedAt = startedAt,
            FinishedAt = finishedAt,
            Success = result.Success && failureKind == AgentFailureKind.None,
            ExitCode = result.ExitCode,
            ExitReason = result.ExitReason,
            FailureKind = failureKind,
            InputTokens = Math.Max(0, result.InputTokens),
            OutputTokens = Math.Max(0, result.OutputTokens),
            EstimatedCost = Math.Max(0m, result.EstimatedCost),
            UsageAccounting = result.UsageAccounting,
            Duration = result.Duration,
            RetryAfter = result.RetryAfter,
            RetryDelay = retryDelay,
            WillRetry = willRetry,
            DecisionReason = decisionReason
        };

    private static AgentRunResult Aggregate(
        AgentRunResult lastResult,
        bool success,
        AgentFailureKind failureKind,
        TimeSpan duration,
        int inputTokens,
        int outputTokens,
        decimal cost) => new()
        {
            Success = success,
            StdOut = lastResult.StdOut,
            StdErr = lastResult.StdErr,
            ExitCode = lastResult.ExitCode,
            Duration = duration,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCost = cost,
            FilesChanged = lastResult.FilesChanged,
            ExitReason = lastResult.ExitReason,
            FailureKind = failureKind,
            RetryAfter = lastResult.RetryAfter
        };

    private static AgentRunResult CancelledResult(
        AgentRunResult lastResult,
        AgentFailureKind failureKind,
        string reason,
        TimeSpan duration,
        int inputTokens,
        int outputTokens,
        decimal cost) => new()
        {
            Success = false,
            StdOut = lastResult.StdOut,
            StdErr = reason,
            ExitCode = lastResult.ExitCode == 0 ? -1 : lastResult.ExitCode,
            Duration = duration,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCost = cost,
            FilesChanged = lastResult.FilesChanged,
            ExitReason = failureKind == AgentFailureKind.Cancelled
            ? "Cancelled"
            : "BudgetExceeded",
            FailureKind = failureKind,
            RetryAfter = lastResult.RetryAfter
        };

    private static AgentExecutionOutcome Outcome(
        AgentRunResult result,
        List<AgentAttemptEvidence> attempts,
        string exhaustionReason)
    {
        var hasAttemptAccounting = attempts.Any(attempt => attempt.UsageAccounting is not null);
        var attemptAccounting = hasAttemptAccounting
            ? attempts.Select(attempt => attempt.UsageAccounting ?? new AgentUsageAccounting
                {
                    Adapter = nameof(AgentExecutionCoordinator),
                    Model = "unreported-attempt",
                    CostComplete = false
                }).ToList()
            : [];
        var accounting = attemptAccounting.Count == 0
            ? result.UsageAccounting
            : AgentUsageAccountingAggregation.Aggregate(
                nameof(AgentExecutionCoordinator),
                result.UsageAccounting?.Model ?? string.Empty,
                attemptAccounting);
        return new AgentExecutionOutcome
        {
            Result = new AgentRunResult
            {
                Success = result.Success,
                StdOut = result.StdOut,
                StdErr = result.StdErr,
                ExitCode = result.ExitCode,
                Duration = result.Duration,
                InputTokens = result.InputTokens,
                OutputTokens = result.OutputTokens,
                EstimatedCost = result.EstimatedCost,
                UsageAccounting = accounting,
                FilesChanged = result.FilesChanged,
                ExitReason = result.ExitReason,
                FailureKind = result.FailureKind,
                RetryAfter = result.RetryAfter
            },
            Attempts = attempts,
            BudgetExhaustionReason = exhaustionReason
        };
    }
}
