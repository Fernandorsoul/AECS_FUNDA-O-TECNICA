using AECS.Application.ControlKernel;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class BudgetEnforcerTests
{
    private readonly BudgetEnforcer _enforcer = new();

    private static AgentRunResult CreateResult(
        int inputTokens = 100, int outputTokens = 100,
        decimal cost = 0.05m, int durationSeconds = 30,
        int filesChanged = 2) => new()
    {
        InputTokens = inputTokens,
        OutputTokens = outputTokens,
        EstimatedCost = cost,
        Duration = TimeSpan.FromSeconds(durationSeconds),
        FilesChanged = Enumerable.Range(0, filesChanged).Select(i => $"file{i}.cs").ToList()
    };

    [Fact]
    public void Check_WithinBudget_ReturnsNull()
    {
        var budget = ExecutionBudget.Default;
        var result = CreateResult();

        var violation = _enforcer.Check(budget, result, 0, result.FilesChanged.Count);

        violation.Should().BeNull();
    }

    [Fact]
    public void Check_TokensExceeded_ReturnsViolation()
    {
        var budget = new ExecutionBudget
        {
            MaxTokens = 100, MaxCostUsd = 1m, MaxRetries = 5,
            MaxDurationSeconds = 300, MaxFilesChanged = 20
        };
        var result = CreateResult(inputTokens: 80, outputTokens: 30);

        var violation = _enforcer.Check(budget, result, 0, result.FilesChanged.Count);

        violation.Should().NotBeNull();
        violation!.LimitType.Should().Be("MaxTokens");
        violation.ActualValue.Should().Be(110);
    }

    [Fact]
    public void Check_CostExceeded_ReturnsViolation()
    {
        var budget = new ExecutionBudget
        {
            MaxTokens = 100000, MaxCostUsd = 0.10m, MaxRetries = 5,
            MaxDurationSeconds = 300, MaxFilesChanged = 20
        };
        var result = CreateResult(cost: 0.15m);

        var violation = _enforcer.Check(budget, result, 0, result.FilesChanged.Count);

        violation.Should().NotBeNull();
        violation!.LimitType.Should().Be("MaxCostUsd");
    }

    [Fact]
    public void Check_RetriesExceeded_ReturnsViolation()
    {
        var budget = new ExecutionBudget
        {
            MaxTokens = 100000, MaxCostUsd = 1m, MaxRetries = 1,
            MaxDurationSeconds = 300, MaxFilesChanged = 20
        };
        var result = CreateResult();

        var violation = _enforcer.Check(budget, result, 3, result.FilesChanged.Count);

        violation.Should().NotBeNull();
        violation!.LimitType.Should().Be("MaxRetries");
    }

    [Fact]
    public void Check_DurationExceeded_ReturnsViolation()
    {
        var budget = new ExecutionBudget
        {
            MaxTokens = 100000, MaxCostUsd = 1m, MaxRetries = 5,
            MaxDurationSeconds = 60, MaxFilesChanged = 20
        };
        var result = CreateResult(durationSeconds: 120);

        var violation = _enforcer.Check(budget, result, 0, result.FilesChanged.Count);

        violation.Should().NotBeNull();
        violation!.LimitType.Should().Be("MaxDurationSeconds");
    }

    [Fact]
    public void Check_FilesChangedExceeded_ReturnsViolation()
    {
        var budget = new ExecutionBudget
        {
            MaxTokens = 100000, MaxCostUsd = 1m, MaxRetries = 5,
            MaxDurationSeconds = 300, MaxFilesChanged = 3
        };
        var result = CreateResult(filesChanged: 5);

        var violation = _enforcer.Check(budget, result, 0, result.FilesChanged.Count);

        violation.Should().NotBeNull();
        violation!.LimitType.Should().Be("MaxFilesChanged");
    }
}
