using AECS.Application;
using AECS.Application.Classification;
using AECS.Application.ControlKernel;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class RejectionFlowTests
{
    private static CandidateChangeSet Candidate(params string[] files) => new()
    {
        AddedFiles = files.ToList(),
        Diff = "diff"
    };

    [Fact]
    public void FullPipeline_ScopeViolation_Rejects()
    {
        // Simula um agente que modificou arquivo proibido
        var contract = new TaskContract
        {
            Id = "T-REJECT",
            Objective = "Modify BillingService",
            Scope = new ScopeDefinition
            {
                Allowed = ["src/Customers/**"],
                Forbidden = ["src/Billing/**"]
            },
            Budget = ExecutionBudget.Default,
            Verification = new VerificationProfile
            {
                Build = true,
                UnitTests = true,
                Scope = true,
                Budget = true
            },
            Approval = new ApprovalPolicy { Production = ApprovalLevel.None }
        };

        var agentResult = new AgentRunResult
        {
            Success = true,
            InputTokens = 500,
            OutputTokens = 300,
            EstimatedCost = 0.05m,
            Duration = TimeSpan.FromSeconds(10),
            FilesChanged = ["src/Billing/BillingService.cs"] // VIOLAÇÃO!
        };

        // 1. ControlKernel detecta violação de scope
        var kernel = new ControlKernel();
        var candidate = Candidate("src/Billing/BillingService.cs");
        var kernelDecision = kernel.ValidateExecution(contract, agentResult, candidate);

        kernelDecision.Allowed.Should().BeFalse();
        kernelDecision.TargetState.Should().Be(TaskState.ScopeViolation);

        // 2. ScopeVerifier também detecta
        var scopeVerifier = new ScopeVerifier();
        var verificationContext = new VerificationContext
        {
            TaskId = contract.Id,
            AgentRunId = "R1",
            Contract = contract,
            AgentResult = agentResult,
            CandidateChangeSet = candidate
        };

        var scopeResult = scopeVerifier.VerifyAsync(verificationContext, CancellationToken.None).Result;
        scopeResult.Status.Should().Be(VerificationStatus.Fail);
        scopeResult.Message.Should().Contain("Scope violations");
    }

    [Fact]
    public void FullPipeline_BudgetExceeded_Rejects()
    {
        var contract = new TaskContract
        {
            Id = "T-BUDGET",
            Objective = "Simple change",
            Budget = new ExecutionBudget
            {
                MaxTokens = 100,
                MaxCostUsd = 0.01m,
                MaxRetries = 1,
                MaxDurationSeconds = 30,
                MaxFilesChanged = 2
            },
            Verification = new VerificationProfile
            {
                Build = true,
                UnitTests = true,
                Scope = true,
                Budget = true
            },
            Approval = new ApprovalPolicy { Production = ApprovalLevel.None }
        };

        var agentResult = new AgentRunResult
        {
            Success = true,
            InputTokens = 5000,
            OutputTokens = 3000,
            EstimatedCost = 0.50m,
            Duration = TimeSpan.FromSeconds(60),
            FilesChanged = ["src/Test.cs"]
        };

        // ControlKernel detecta budget excedido
        var kernel = new ControlKernel();
        var candidate = Candidate("src/Test.cs");
        var kernelDecision = kernel.ValidateExecution(contract, agentResult, candidate);

        kernelDecision.Allowed.Should().BeFalse();
        kernelDecision.TargetState.Should().Be(TaskState.BudgetExceeded);

        // BudgetVerifier também detecta
        var budgetVerifier = new BudgetVerifier();
        var verificationContext = new VerificationContext
        {
            TaskId = contract.Id,
            AgentRunId = "R1",
            Contract = contract,
            AgentResult = agentResult,
            CandidateChangeSet = candidate
        };

        var budgetResult = budgetVerifier.VerifyAsync(verificationContext, CancellationToken.None).Result;
        budgetResult.Status.Should().Be(VerificationStatus.Fail);
        budgetResult.Message.Should().Contain("Token limit exceeded");
    }

    [Fact]
    public void FullPipeline_AllPass_Verifies()
    {
        var contract = new TaskContract
        {
            Id = "T-OK",
            Objective = "Fix null handling",
            Scope = new ScopeDefinition
            {
                Allowed = ["src/Customers/**"]
            },
            Budget = ExecutionBudget.Default,
            Verification = new VerificationProfile
            {
                Build = true,
                UnitTests = true,
                Scope = true,
                Budget = true
            },
            Approval = new ApprovalPolicy { Production = ApprovalLevel.None }
        };

        var agentResult = new AgentRunResult
        {
            Success = true,
            InputTokens = 500,
            OutputTokens = 300,
            EstimatedCost = 0.05m,
            Duration = TimeSpan.FromSeconds(10),
            FilesChanged = ["src/Customers/CustomerMapper.cs"]
        };

        // ControlKernel permite
        var kernel = new ControlKernel();
        var candidate = Candidate("src/Customers/CustomerMapper.cs");
        var kernelDecision = kernel.ValidateExecution(contract, agentResult, candidate);
        kernelDecision.Allowed.Should().BeTrue();

        // Todos os verificadores passam
        var context = new VerificationContext
        {
            TaskId = contract.Id,
            AgentRunId = "R1",
            Contract = contract,
            AgentResult = agentResult,
            CandidateChangeSet = candidate
        };

        var scopeResult = new ScopeVerifier().VerifyAsync(context, CancellationToken.None).Result;
        scopeResult.Status.Should().Be(VerificationStatus.Pass);

        var budgetResult = new BudgetVerifier().VerifyAsync(context, CancellationToken.None).Result;
        budgetResult.Status.Should().Be(VerificationStatus.Pass);

        // DecisionEngine aprova
        var decisionEngine = new DecisionEngine();
        var decision = decisionEngine.Decide(
            [
                new VerificationResult { Verifier = "AgentSuccess", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Application", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "NonEmptyChange", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Build", Status = VerificationStatus.Pass },
                new VerificationResult { Verifier = "Tests", Status = VerificationStatus.Pass },
                new VerificationResult
                {
                    Verifier = AECS.Application.Verification.ConstraintLedgerVerifier.Name,
                    Status = VerificationStatus.Pass
                },
                scopeResult,
                budgetResult
            ],
            contract);

        decision.Decision.Should().Be(TaskDecision.Verified);
    }
}
