using AECS.Application.ControlKernel;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class ControlKernelTests
{
    private readonly ControlKernel _kernel = new();

    private static TaskContract CreateContract(
        ApprovalLevel approval = ApprovalLevel.None,
        string[]? allowed = null) => new()
    {
        Id = "T1",
        Objective = "Test",
        Budget = ExecutionBudget.Default,
        Scope = new ScopeDefinition
        {
            Allowed = allowed?.ToList() ?? ["src/**"]
        },
        Approval = new ApprovalPolicy { Production = approval }
    };

    private static AgentRunResult CreateResult(
        string[]? files = null) => new()
    {
        InputTokens = 100,
        OutputTokens = 100,
        EstimatedCost = 0.05m,
        Duration = TimeSpan.FromSeconds(30),
        FilesChanged = files?.ToList() ?? ["src/Test.cs"]
    };

    private static CandidateChangeSet Candidate(AgentRunResult result) => new()
    {
        AddedFiles = result.FilesChanged.ToList(),
        Diff = "diff"
    };

    [Fact]
    public void ValidateExecution_AllPassed_ReturnsVerifying()
    {
        var result = CreateResult();
        var decision = _kernel.ValidateExecution(CreateContract(), result, Candidate(result));

        decision.Allowed.Should().BeTrue();
        decision.TargetState.Should().Be(TaskState.Verifying);
    }

    [Fact]
    public void ValidateExecution_HumanApproval_ReturnsHumanReview()
    {
        var contract = CreateContract(approval: ApprovalLevel.Human);
        var result = CreateResult();
        var decision = _kernel.ValidateExecution(contract, result, Candidate(result));

        decision.Allowed.Should().BeTrue();
        decision.TargetState.Should().Be(TaskState.HumanReviewRequired);
    }

    [Fact]
    public void ValidateExecution_BudgetExceeded_ReturnsBudgetExceeded()
    {
        var contract = CreateContract();
        var result = new AgentRunResult
        {
            InputTokens = 50000,
            OutputTokens = 50000,
            EstimatedCost = 0.05m,
            Duration = TimeSpan.FromSeconds(30),
            FilesChanged = ["src/Test.cs"]
        };

        var decision = _kernel.ValidateExecution(contract, result, Candidate(result));

        decision.Allowed.Should().BeFalse();
        decision.TargetState.Should().Be(TaskState.BudgetExceeded);
    }

    [Fact]
    public void ValidateExecution_ScopeViolation_ReturnsScopeViolation()
    {
        var contract = CreateContract(allowed: ["src/Customers/**"]);
        var result = CreateResult(files: ["src/Billing/Billing.cs"]);

        var decision = _kernel.ValidateExecution(contract, result, Candidate(result));

        decision.Allowed.Should().BeFalse();
        decision.TargetState.Should().Be(TaskState.ScopeViolation);
    }

    [Fact]
    public void ValidateExecution_Timeout_ReturnsTimedOut()
    {
        var contract = new TaskContract
        {
            Id = "T1",
            Budget = new ExecutionBudget { MaxDurationSeconds = 10 },
            Scope = new ScopeDefinition { Allowed = ["src/**"] }
        };
        var result = new AgentRunResult
        {
            Duration = TimeSpan.FromSeconds(60),
            FilesChanged = ["src/Test.cs"]
        };

        var decision = _kernel.ValidateExecution(contract, result, Candidate(result));

        decision.Allowed.Should().BeFalse();
        decision.TargetState.Should().Be(TaskState.TimedOut);
    }
}
