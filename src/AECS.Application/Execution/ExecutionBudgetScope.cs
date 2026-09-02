using System.Diagnostics;
using AECS.Domain.Models;

namespace AECS.Application.Execution;

public sealed class ExecutionBudgetScope : IDisposable
{
    private readonly CancellationToken _callerToken;
    private readonly CancellationTokenSource _wallClockSource;
    private readonly CancellationTokenSource _linkedSource;
    private readonly Stopwatch _stopwatch = Stopwatch.StartNew();
    private readonly TimeSpan _wallClockLimit;

    public ExecutionBudgetScope(ExecutionBudget budget, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(budget);
        if (budget.MaxTokens < 0)
            throw new ArgumentOutOfRangeException(nameof(budget), "Token budget cannot be negative.");
        if (budget.MaxCostUsd < 0)
            throw new ArgumentOutOfRangeException(nameof(budget), "Cost budget cannot be negative.");
        if (budget.MaxRetries < 0)
            throw new ArgumentOutOfRangeException(nameof(budget), "Retry budget cannot be negative.");
        if (budget.MaxDurationSeconds <= 0)
            throw new ArgumentOutOfRangeException(nameof(budget), "Wall-clock budget must be positive.");

        Budget = budget;
        _callerToken = callerToken;
        _wallClockLimit = TimeSpan.FromSeconds(budget.MaxDurationSeconds);
        _wallClockSource = new CancellationTokenSource(_wallClockLimit);
        _linkedSource = CancellationTokenSource.CreateLinkedTokenSource(
            callerToken,
            _wallClockSource.Token);
        StartedAt = DateTime.UtcNow;
    }

    public ExecutionBudget Budget { get; }
    public DateTime StartedAt { get; }
    public CancellationToken Token => _linkedSource.Token;
    public bool CallerCancellationRequested => _callerToken.IsCancellationRequested;
    public bool WallClockExhausted =>
        _wallClockSource.IsCancellationRequested || RemainingDuration <= TimeSpan.Zero;
    public TimeSpan Elapsed => _stopwatch.Elapsed;
    public int MaximumAttempts => Budget.MaxRetries == int.MaxValue
        ? int.MaxValue
        : Budget.MaxRetries + 1;

    public TimeSpan RemainingDuration
    {
        get
        {
            var remaining = _wallClockLimit - _stopwatch.Elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    public TimeSpan LimitTimeout(TimeSpan requested)
    {
        var remaining = RemainingDuration;
        if (remaining <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return requested <= remaining ? requested : remaining;
    }

    public ExecutionBudget RemainingAgentBudget(int inputTokens, int outputTokens, decimal cost) => new()
    {
        MaxTokens = Math.Max(0, Budget.MaxTokens - inputTokens - outputTokens),
        MaxCostUsd = Math.Max(0m, Budget.MaxCostUsd - cost),
        MaxRetries = Budget.MaxRetries,
        MaxDurationSeconds = Math.Max(1, (int)Math.Ceiling(RemainingDuration.TotalSeconds)),
        MaxFilesChanged = Budget.MaxFilesChanged
    };

    public void Dispose()
    {
        _stopwatch.Stop();
        _linkedSource.Dispose();
        _wallClockSource.Dispose();
    }
}
