using AECS.Application.ControlKernel;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class CircuitBreakerTests
{
    private readonly CircuitBreaker _breaker = new();

    private static TaskContract CreateContract(
        int maxTokens = 60000, decimal maxCost = 0.20m,
        int maxRetries = 1, int maxDuration = 120,
        int maxFiles = 10, string[]? allowed = null, string[]? forbidden = null) => new()
    {
        Id = "T1",
        Objective = "Test",
        Budget = new ExecutionBudget
        {
            MaxTokens = maxTokens,
            MaxCostUsd = maxCost,
            MaxRetries = maxRetries,
            MaxDurationSeconds = maxDuration,
            MaxFilesChanged = maxFiles
        },
        Scope = new ScopeDefinition
        {
            Allowed = allowed?.ToList() ?? ["src/**"],
            Forbidden = forbidden?.ToList() ?? []
        }
    };

    private static AgentRunResult CreateResult(
        int tokens = 200, decimal cost = 0.05m,
        int durationSec = 30, string[]? files = null) => new()
    {
        InputTokens = tokens / 2,
        OutputTokens = tokens / 2,
        EstimatedCost = cost,
        Duration = TimeSpan.FromSeconds(durationSec),
        FilesChanged = files?.ToList() ?? ["src/Test.cs"]
    };

    [Fact]
    public void Check_WithinLimits_NotTripped()
    {
        var result = _breaker.Check(CreateContract(), CreateResult(), 0);

        result.Tripped.Should().BeFalse();
    }

    [Fact]
    public void Check_BudgetExceeded_Tripped()
    {
        var contract = CreateContract(maxTokens: 100);
        var result = CreateResult(tokens: 500);

        var breakerResult = _breaker.Check(contract, result, 0);

        breakerResult.Tripped.Should().BeTrue();
        breakerResult.LimitType.Should().Be("MaxTokens");
        breakerResult.Evidence.Should().NotBeNull();
        breakerResult.Evidence!.EventType.Should().Be("BudgetExceeded");
    }

    [Fact]
    public void Check_ScopeViolation_Tripped()
    {
        var contract = CreateContract(allowed: ["src/Customers/**"]);
        var result = CreateResult(files: ["src/Billing/Billing.cs"]);

        var breakerResult = _breaker.Check(contract, result, 0);

        breakerResult.Tripped.Should().BeTrue();
        breakerResult.LimitType.Should().Be("ScopeViolation");
        breakerResult.Evidence.Should().NotBeNull();
        breakerResult.Evidence!.EventType.Should().Be("ScopeViolation");
    }

    [Fact]
    public void Check_Timeout_Tripped()
    {
        var contract = CreateContract(maxDuration: 60);
        var result = CreateResult(durationSec: 120);

        var breakerResult = _breaker.Check(contract, result, 0);

        breakerResult.Tripped.Should().BeTrue();
        breakerResult.LimitType.Should().Be("MaxDurationSeconds");
    }
}
