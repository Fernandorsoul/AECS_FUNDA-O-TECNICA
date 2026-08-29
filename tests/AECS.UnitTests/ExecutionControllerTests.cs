using AECS.Domain.Enums;
using AECS.Domain.Models;
using AECS.Application;
using FluentAssertions;

namespace AECS.UnitTests;

public class ExecutionControllerTests
{
    private readonly ExecutionController _controller = new();

    [Fact]
    public async Task PlanAsync_R0Risk_SelectsSmallModel()
    {
        var contract = new TaskContract
        {
            Id = "T1",
            Objective = "Add comments to CustomerMapper",
            Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R0 }
        };

        var plan = await _controller.PlanAsync(contract, CancellationToken.None);

        plan.Model.Should().Be("qwen2.5-coder:7b");
        plan.Risk.Should().Be(RiskLevel.R0);
        plan.TaskId.Should().Be("T1");
    }

    [Fact]
    public async Task PlanAsync_R1Risk_SelectsSmallModel()
    {
        var contract = new TaskContract
        {
            Id = "T2",
            Objective = "Fix null handling",
            Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R1 }
        };

        var plan = await _controller.PlanAsync(contract, CancellationToken.None);

        plan.Model.Should().Be("qwen2.5-coder:7b");
        plan.Risk.Should().Be(RiskLevel.R1);
    }

    [Fact]
    public async Task PlanAsync_R2Risk_SelectsLargerModel()
    {
        var contract = new TaskContract
        {
            Id = "T3",
            Objective = "Add validation service",
            Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R2 }
        };

        var plan = await _controller.PlanAsync(contract, CancellationToken.None);

        plan.Model.Should().Be("qwen2.5-coder:7b");
        plan.Risk.Should().Be(RiskLevel.R2);
    }

    [Fact]
    public async Task PlanAsync_R3Risk_SelectsLargerModel()
    {
        var contract = new TaskContract
        {
            Id = "T4",
            Objective = "Implement authentication",
            Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R3 }
        };

        var plan = await _controller.PlanAsync(contract, CancellationToken.None);

        plan.Model.Should().Be("qwen2.5-coder:7b");
        plan.Risk.Should().Be(RiskLevel.R3);
    }

    [Fact]
    public async Task PlanAsync_PreservesBudget()
    {
        var budget = new ExecutionBudget
        {
            MaxTokens = 100000,
            MaxCostUsd = 0.50m,
            MaxRetries = 3,
            MaxDurationSeconds = 300,
            MaxFilesChanged = 15
        };

        var contract = new TaskContract
        {
            Id = "T5",
            Objective = "Test",
            Budget = budget
        };

        var plan = await _controller.PlanAsync(contract, CancellationToken.None);

        plan.Budget.Should().Be(budget);
    }

    [Fact]
    public async Task PlanAsync_PreservesVerificationProfile()
    {
        var verification = new VerificationProfile
        {
            Build = true,
            UnitTests = true,
            IntegrationTests = true,
            Scope = true,
            SecurityScan = true
        };

        var contract = new TaskContract
        {
            Id = "T6",
            Objective = "Test",
            Verification = verification
        };

        var plan = await _controller.PlanAsync(contract, CancellationToken.None);

        plan.Verification.Should().Be(verification);
    }
}
