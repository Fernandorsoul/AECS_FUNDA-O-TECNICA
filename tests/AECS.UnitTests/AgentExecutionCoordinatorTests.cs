using AECS.Application.Execution;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class AgentExecutionCoordinatorTests
{
    [Fact]
    public async Task RateLimit429_UsesRetryAfter_ThenSucceeds()
    {
        var adapter = new SequenceAgent(
            Failure(exitCode: 429, retryAfter: TimeSpan.FromSeconds(3)),
            Success(inputTokens: 20, outputTokens: 10));
        var delays = new List<TimeSpan>();
        var coordinator = new AgentExecutionCoordinator(
            adapter,
            (delay, _) =>
            {
                delays.Add(delay);
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(10));
        using var budget = new ExecutionBudgetScope(
            Budget(retries: 2),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        outcome.Result.Success.Should().BeTrue();
        outcome.Attempts.Should().HaveCount(2);
        outcome.Attempts[0].FailureKind.Should().Be(AgentFailureKind.RateLimited);
        outcome.Attempts[0].WillRetry.Should().BeTrue();
        outcome.Attempts[0].RetryDelay.Should().Be(TimeSpan.FromSeconds(3));
        outcome.Attempts[1].Success.Should().BeTrue();
        outcome.Result.UsageAccounting!.Components.Should().HaveCount(2);
        outcome.Result.UsageAccounting.AccountedCostUsd.Should().Be(0.011m);
        delays.Should().ContainSingle().Which.Should().Be(TimeSpan.FromSeconds(3));
        adapter.Requests[1].Budget.MaxTokens.Should().BeLessThan(
            adapter.Requests[0].Budget.MaxTokens);
    }

    [Fact]
    public async Task Timeout_IsRetried_AndLaterSuccessIsReturned()
    {
        var adapter = new SequenceAgent(
            Failure(AgentFailureKind.Timeout, "Timeout"),
            Success());
        var coordinator = NoWaitCoordinator(adapter);
        using var budget = new ExecutionBudgetScope(
            Budget(retries: 1),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        adapter.CallCount.Should().Be(2);
        outcome.Result.Success.Should().BeTrue();
        outcome.Attempts[0].WillRetry.Should().BeTrue();
        outcome.Attempts[0].FailureKind.Should().Be(AgentFailureKind.Timeout);
    }

    [Fact]
    public async Task RetryWithMissingAttemptAccounting_RemainsIncomplete()
    {
        var adapter = new SequenceAgent(
            new AgentRunResult
            {
                Success = false,
                ExitCode = 500,
                ExitReason = "ApiError",
                FailureKind = AgentFailureKind.Transient
            },
            Success());
        var coordinator = NoWaitCoordinator(adapter);
        using var budget = new ExecutionBudgetScope(
            Budget(retries: 1),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        outcome.Result.Success.Should().BeTrue();
        outcome.Result.UsageAccounting!.CostComplete.Should().BeFalse();
        outcome.Result.UsageAccounting.RateCardEstimatedCostUsd.Should().Be(0.01m);
        outcome.Result.UsageAccounting.AccountedCostUsd.Should().BeNull();
    }

    [Fact]
    public async Task Cancellation_IsNeverRetried()
    {
        var adapter = new SequenceAgent(Failure(
            AgentFailureKind.Cancelled,
            "Cancelled"));
        var coordinator = NoWaitCoordinator(adapter);
        using var budget = new ExecutionBudgetScope(
            Budget(retries: 3),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        adapter.CallCount.Should().Be(1);
        outcome.Result.FailureKind.Should().Be(AgentFailureKind.Cancelled);
        outcome.Attempts.Should().ContainSingle(attempt =>
            !attempt.WillRetry &&
            attempt.DecisionReason.Contains("permanent"));
    }

    [Theory]
    [InlineData(AgentFailureKind.Permanent)]
    [InlineData(AgentFailureKind.PolicyViolation)]
    [InlineData(AgentFailureKind.BudgetExceeded)]
    public async Task PermanentOrPolicyFailure_IsNeverRetried(
        AgentFailureKind failureKind)
    {
        var adapter = new SequenceAgent(Failure(failureKind, failureKind.ToString()));
        var coordinator = NoWaitCoordinator(adapter);
        using var budget = new ExecutionBudgetScope(
            Budget(retries: 3),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        adapter.CallCount.Should().Be(1);
        outcome.Attempts.Should().ContainSingle(attempt => !attempt.WillRetry);
    }

    [Fact]
    public async Task RetryLimit_AllowsOnlyInitialAttemptPlusDeclaredRetries()
    {
        var adapter = new SequenceAgent(
            Failure(AgentFailureKind.Transient),
            Failure(AgentFailureKind.Transient),
            Failure(AgentFailureKind.Transient),
            Success());
        var coordinator = NoWaitCoordinator(adapter);
        using var budget = new ExecutionBudgetScope(
            Budget(retries: 2),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        adapter.CallCount.Should().Be(3);
        outcome.Attempts.Should().HaveCount(3);
        outcome.Result.Success.Should().BeFalse();
        outcome.BudgetExhaustionReason.Should().Contain("maximum attempts");
    }

    [Fact]
    public async Task ConsumedTokenBudget_BlocksNextAttempt()
    {
        var adapter = new SequenceAgent(
            Failure(
                AgentFailureKind.Transient,
                inputTokens: 60,
                outputTokens: 40),
            Success());
        var coordinator = NoWaitCoordinator(adapter);
        using var budget = new ExecutionBudgetScope(
            Budget(tokens: 100, retries: 2),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        adapter.CallCount.Should().Be(1);
        outcome.Result.FailureKind.Should().Be(AgentFailureKind.BudgetExceeded);
        outcome.BudgetExhaustionReason.Should().Contain("Token budget exhausted");
        outcome.Attempts.Should().ContainSingle(attempt => !attempt.WillRetry);
    }

    [Fact]
    public async Task ZeroTokenBudget_BlocksInitialAttempt()
    {
        var adapter = new SequenceAgent(Success());
        var coordinator = NoWaitCoordinator(adapter);
        using var budget = new ExecutionBudgetScope(
            Budget(tokens: 0, retries: 2),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        adapter.CallCount.Should().Be(0);
        outcome.Attempts.Should().BeEmpty();
        outcome.Result.FailureKind.Should().Be(AgentFailureKind.BudgetExceeded);
    }

    [Fact]
    public async Task RetryAfterBeyondRemainingWallClock_ExhaustsBudgetWithoutWaiting()
    {
        var adapter = new SequenceAgent(Failure(
            AgentFailureKind.RateLimited,
            retryAfter: TimeSpan.FromSeconds(60)));
        var delayCalled = false;
        var coordinator = new AgentExecutionCoordinator(
            adapter,
            (_, _) =>
            {
                delayCalled = true;
                return Task.CompletedTask;
            },
            TimeSpan.FromSeconds(30));
        using var budget = new ExecutionBudgetScope(
            Budget(retries: 2, durationSeconds: 1),
            CancellationToken.None);

        var outcome = await coordinator.ExecuteAsync(Request(budget.Budget), budget);

        delayCalled.Should().BeFalse();
        adapter.CallCount.Should().Be(1);
        outcome.Result.FailureKind.Should().Be(AgentFailureKind.BudgetExceeded);
        outcome.BudgetExhaustionReason.Should().Contain("Wall-clock");
        outcome.Attempts.Should().ContainSingle()
            .Which.RetryDelay.Should().Be(TimeSpan.FromSeconds(30));
    }

    private static AgentExecutionCoordinator NoWaitCoordinator(IAgentAdapter adapter) =>
        new(adapter, (_, _) => Task.CompletedTask);

    private static AgentExecutionRequest Request(ExecutionBudget budget) => new()
    {
        TaskId = "T-RETRY",
        Objective = "Exercise retry policy",
        RepoPath = Path.GetTempPath(),
        Budget = budget
    };

    private static ExecutionBudget Budget(
        int tokens = 1000,
        decimal cost = 1m,
        int retries = 1,
        int durationSeconds = 60) => new()
        {
            MaxTokens = tokens,
            MaxCostUsd = cost,
            MaxRetries = retries,
            MaxDurationSeconds = durationSeconds,
            MaxFilesChanged = 10
        };

    private static AgentRunResult Success(
        int inputTokens = 10,
        int outputTokens = 10) => new()
        {
            Success = true,
            ExitCode = 0,
            ExitReason = "Completed",
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCost = 0.01m,
            UsageAccounting = new AgentUsageAccounting
            {
                Adapter = "scripted",
                Model = "success",
                RateCardEstimatedCostUsd = 0.01m,
                CostComplete = true
            },
            Duration = TimeSpan.FromMilliseconds(5),
            FilesChanged = ["src/Changed.cs"]
        };

    private static AgentRunResult Failure(
        AgentFailureKind failureKind = AgentFailureKind.None,
        string exitReason = "ApiError",
        int exitCode = 500,
        int inputTokens = 5,
        int outputTokens = 5,
        TimeSpan? retryAfter = null) => new()
        {
            Success = false,
            ExitCode = exitCode,
            ExitReason = exitReason,
            FailureKind = failureKind,
            InputTokens = inputTokens,
            OutputTokens = outputTokens,
            EstimatedCost = 0.001m,
            UsageAccounting = new AgentUsageAccounting
            {
                Adapter = "scripted",
                Model = "failure",
                RateCardEstimatedCostUsd = 0.001m,
                CostComplete = true
            },
            Duration = TimeSpan.FromMilliseconds(5),
            RetryAfter = retryAfter,
            StdErr = "scripted failure"
        };

    private sealed class SequenceAgent : IAgentAdapter
    {
        private readonly Queue<AgentRunResult> _results;

        public SequenceAgent(params AgentRunResult[] results)
        {
            _results = new Queue<AgentRunResult>(results);
        }

        public int CallCount { get; private set; }
        public List<AgentExecutionRequest> Requests { get; } = [];

        public Task<AgentRunResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
