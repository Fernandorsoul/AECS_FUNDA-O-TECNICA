using AECS.Application.AdaptiveController;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class ExecutionHistoryStoreTests
{
    [Fact]
    public void Add_IncreasesCount()
    {
        var store = new ExecutionHistoryStore();
        store.Add(new ExecutionRecord { TaskId = "T1", Model = "codellama:3b", Risk = RiskLevel.R1, Decision = TaskDecision.Verified });

        store.Count.Should().Be(1);
    }

    [Fact]
    public void GetByRisk_FiltersCorrectly()
    {
        var store = new ExecutionHistoryStore();
        store.Add(new ExecutionRecord { TaskId = "T1", Risk = RiskLevel.R1, Model = "m1" });
        store.Add(new ExecutionRecord { TaskId = "T2", Risk = RiskLevel.R2, Model = "m1" });
        store.Add(new ExecutionRecord { TaskId = "T3", Risk = RiskLevel.R1, Model = "m1" });

        var r1Records = store.GetByRisk(RiskLevel.R1);

        r1Records.Should().HaveCount(2);
    }

    [Fact]
    public void GetModelStats_GroupsByModel()
    {
        var store = new ExecutionHistoryStore();
        store.Add(new ExecutionRecord { Model = "m1", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.1m });
        store.Add(new ExecutionRecord { Model = "m1", Risk = RiskLevel.R1, Decision = TaskDecision.Rejected, Cost = 0.2m });
        store.Add(new ExecutionRecord { Model = "m2", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.05m });

        var stats = store.GetModelStats();

        stats.Should().HaveCount(2);
        var m1 = stats.First(s => s.Model == "m1");
        m1.TotalRuns.Should().Be(2);
        m1.SuccessfulRuns.Should().Be(1);
        m1.SuccessRate.Should().Be(0.5);
    }
}

public class HeuristicModelSelectorTests
{
    [Fact]
    public void SelectModel_InsufficientData_ReturnsDefault()
    {
        var store = new ExecutionHistoryStore();
        var selector = new HeuristicModelSelector(store);

        var model = selector.SelectModel(RiskLevel.R1, "Fix null handling");

        model.Should().Be("codellama:3b");
    }

    [Fact]
    public void SelectModel_WithData_SelectsBestModel()
    {
        var store = new ExecutionHistoryStore();
        // m1: 2/3 success
        store.Add(new ExecutionRecord { Model = "m1", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.1m });
        store.Add(new ExecutionRecord { Model = "m1", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.1m });
        store.Add(new ExecutionRecord { Model = "m1", Risk = RiskLevel.R1, Decision = TaskDecision.Rejected, Cost = 0.1m });
        // m2: 1/2 success
        store.Add(new ExecutionRecord { Model = "m2", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.05m });
        store.Add(new ExecutionRecord { Model = "m2", Risk = RiskLevel.R1, Decision = TaskDecision.Rejected, Cost = 0.05m });

        var selector = new HeuristicModelSelector(store, minSamples: 3);
        var model = selector.SelectModel(RiskLevel.R1, "Fix null handling");

        model.Should().Be("m1"); // Higher success rate
    }

    [Fact]
    public void SelectModel_SameSuccessRate_PrefersCheaper()
    {
        var store = new ExecutionHistoryStore();
        store.Add(new ExecutionRecord { Model = "expensive", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.5m });
        store.Add(new ExecutionRecord { Model = "expensive", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.5m });
        store.Add(new ExecutionRecord { Model = "cheap", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.01m });
        store.Add(new ExecutionRecord { Model = "cheap", Risk = RiskLevel.R1, Decision = TaskDecision.Verified, Cost = 0.01m });

        var selector = new HeuristicModelSelector(store, minSamples: 3);
        var model = selector.SelectModel(RiskLevel.R1, "Fix null handling");

        model.Should().Be("cheap");
    }
}

public class HeuristicBudgetOptimizerTests
{
    [Fact]
    public void Optimize_InsufficientData_ReturnsRequested()
    {
        var store = new ExecutionHistoryStore();
        var optimizer = new HeuristicBudgetOptimizer(store);
        var requested = ExecutionBudget.Default;

        var optimized = optimizer.Optimize(RiskLevel.R1, requested);

        optimized.Should().Be(requested);
    }

    [Fact]
    public void Optimize_WithData_AdjustsBudget()
    {
        var store = new ExecutionHistoryStore();
        // Successful runs using ~1000 tokens
        for (int i = 0; i < 5; i++)
        {
            store.Add(new ExecutionRecord
            {
                Risk = RiskLevel.R1,
                Decision = TaskDecision.Verified,
                InputTokens = 500,
                OutputTokens = 500,
                Duration = TimeSpan.FromSeconds(30),
                Cost = 0.01m
            });
        }

        var optimizer = new HeuristicBudgetOptimizer(store, minSamples: 3);
        var requested = new ExecutionBudget { MaxTokens = 500, MaxDurationSeconds = 60 };
        var optimized = optimizer.Optimize(RiskLevel.R1, requested);

        // Should increase tokens since p75 * 1.5 > 500
        optimized.MaxTokens.Should().BeGreaterThan(500);
    }
}

public class AdaptiveControllerTests
{
    [Fact]
    public async Task PlanAsync_InsufficientData_UsesFallback()
    {
        var store = new ExecutionHistoryStore();
        var controller = new AdaptiveController(store);
        var contract = new TaskContract
        {
            Id = "T1",
            Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R1 }
        };

        var plan = await controller.PlanAsync(contract, CancellationToken.None);

        plan.Model.Should().Be("qwen2.5-coder:7b"); // Fallback default
    }

    [Fact]
    public async Task PlanAsync_WithData_UsesAdaptive()
    {
        var store = new ExecutionHistoryStore();
        // Add enough data
        for (int i = 0; i < 6; i++)
        {
            store.Add(new ExecutionRecord
            {
                Model = "custom-model",
                Risk = RiskLevel.R1,
                Decision = TaskDecision.Verified,
                InputTokens = 100,
                OutputTokens = 100,
                Cost = 0.01m,
                Duration = TimeSpan.FromSeconds(10)
            });
        }

        var controller = new AdaptiveController(store);
        var contract = new TaskContract
        {
            Id = "T1",
            Budget = ExecutionBudget.Default,
            Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R1 }
        };

        var plan = await controller.PlanAsync(contract, CancellationToken.None);

        plan.Model.Should().Be("custom-model");
    }
}
