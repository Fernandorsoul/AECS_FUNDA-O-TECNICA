using AECS.Application;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class DecisionEngineTests
{
    private readonly DecisionEngine _engine = new();

    private static TaskContract CreateContract(
        ApprovalLevel approval = ApprovalLevel.None,
        bool buildRequired = true, bool testsRequired = true,
        bool scopeRequired = true, bool budgetRequired = true) => new()
    {
        Id = "T1",
        Objective = "Test",
        Verification = new VerificationProfile
        {
            Build = buildRequired,
            UnitTests = testsRequired,
            Scope = scopeRequired,
            Budget = budgetRequired
        },
        Approval = new ApprovalPolicy { Production = approval }
    };

    private static VerificationResult CreatePassResult(string verifier) => new()
    {
        Verifier = verifier,
        Status = VerificationStatus.Pass,
        Severity = Severity.Info,
        Message = "OK"
    };

    private static VerificationResult CreateFailResult(string verifier) => new()
    {
        Verifier = verifier,
        Status = VerificationStatus.Fail,
        Severity = Severity.Error,
        Message = "Failed"
    };

    [Fact]
    public void Decide_AllPass_ReturnsVerified()
    {
        var results = new List<VerificationResult>
        {
            CreatePassResult("Build"),
            CreatePassResult("Tests"),
            CreatePassResult("Scope"),
            CreatePassResult("Budget")
        };

        var decision = _engine.Decide(results, CreateContract());

        decision.Decision.Should().Be(TaskDecision.Verified);
        decision.TargetState.Should().Be(TaskState.Verified);
    }

    [Fact]
    public void Decide_BuildFails_ReturnsRejected()
    {
        var results = new List<VerificationResult>
        {
            CreateFailResult("Build"),
            CreatePassResult("Tests"),
            CreatePassResult("Scope"),
            CreatePassResult("Budget")
        };

        var decision = _engine.Decide(results, CreateContract());

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.TargetState.Should().Be(TaskState.Rejected);
        decision.Failures.Should().Contain(f => f.Contains("Build"));
    }

    [Fact]
    public void Decide_TestsFail_ReturnsRejected()
    {
        var results = new List<VerificationResult>
        {
            CreatePassResult("Build"),
            CreateFailResult("Tests"),
            CreatePassResult("Scope"),
            CreatePassResult("Budget")
        };

        var decision = _engine.Decide(results, CreateContract());

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().Contain(f => f.Contains("Tests"));
    }

    [Fact]
    public void Decide_ScopeFails_ReturnsRejected()
    {
        var results = new List<VerificationResult>
        {
            CreatePassResult("Build"),
            CreatePassResult("Tests"),
            CreateFailResult("Scope"),
            CreatePassResult("Budget")
        };

        var decision = _engine.Decide(results, CreateContract());

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().Contain(f => f.Contains("Scope"));
    }

    [Fact]
    public void Decide_AllPass_HumanApproval_ReturnsHumanReview()
    {
        var results = new List<VerificationResult>
        {
            CreatePassResult("Build"),
            CreatePassResult("Tests"),
            CreatePassResult("Scope"),
            CreatePassResult("Budget")
        };

        var decision = _engine.Decide(results, CreateContract(approval: ApprovalLevel.Human));

        decision.Decision.Should().Be(TaskDecision.HumanReviewRequired);
        decision.TargetState.Should().Be(TaskState.HumanReviewRequired);
    }

    [Fact]
    public void Decide_MultipleFailures_ReturnsAllFailures()
    {
        var results = new List<VerificationResult>
        {
            CreateFailResult("Build"),
            CreateFailResult("Tests"),
            CreatePassResult("Scope"),
            CreatePassResult("Budget")
        };

        var decision = _engine.Decide(results, CreateContract());

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().HaveCount(2);
    }

    [Fact]
    public void Decide_OptionalVerifierFails_StillVerified()
    {
        var results = new List<VerificationResult>
        {
            CreatePassResult("Build"),
            CreatePassResult("Tests"),
            CreatePassResult("Scope"),
            CreatePassResult("Budget"),
            CreateFailResult("SecurityScan") // optional
        };

        var contract = CreateContract();
        var decision = _engine.Decide(results, contract);

        decision.Decision.Should().Be(TaskDecision.Verified);
    }
}
