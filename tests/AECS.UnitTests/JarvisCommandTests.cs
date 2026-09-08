using AECS.Application;
using AECS.Application.Classification;
using AECS.Cli.Jarvis;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class JarvisCommandTests
{
    private readonly RiskClassifier _classifier = new();
    private readonly DecisionEngine _decisionEngine = new();

    [Fact]
    public void RiskCommand_SimpleTask_ReturnsR1()
    {
        var contract = new TaskContract
        {
            Id = "T1",
            Objective = "Fix null handling in CustomerMapper",
            Scope = new ScopeDefinition { Allowed = ["src/Customers/**"] }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R1);
    }

    [Fact]
    public void RiskCommand_AuthTask_ReturnsR3()
    {
        var contract = new TaskContract
        {
            Id = "T2",
            Objective = "Implement JWT authentication",
            Scope = new ScopeDefinition { Allowed = ["src/Auth/**"] }
        };

        var risk = _classifier.Classify(contract);

        risk.Should().Be(RiskLevel.R3);
    }

    [Fact]
    public void ExplainCommand_AllPass_ReturnsVerified()
    {
        var results = new List<VerificationResult>
        {
            new() { Verifier = "AgentSuccess", Status = VerificationStatus.Pass },
            new() { Verifier = "Application", Status = VerificationStatus.Pass },
            new() { Verifier = "NonEmptyChange", Status = VerificationStatus.Pass },
            new() { Verifier = "Build", Status = VerificationStatus.Pass },
            new() { Verifier = "Tests", Status = VerificationStatus.Pass },
            new() { Verifier = "Scope", Status = VerificationStatus.Pass },
            new() { Verifier = "Budget", Status = VerificationStatus.Pass }
        };

        var contract = new TaskContract
        {
            Verification = new VerificationProfile { Build = true, UnitTests = true, Scope = true, Budget = true },
            Approval = new ApprovalPolicy { Production = ApprovalLevel.None }
        };

        var decision = _decisionEngine.Decide(results, contract);

        decision.Decision.Should().Be(TaskDecision.Verified);
    }

    [Fact]
    public void ExplainCommand_BuildFails_ReturnsRejected()
    {
        var results = new List<VerificationResult>
        {
            new() { Verifier = "AgentSuccess", Status = VerificationStatus.Pass },
            new() { Verifier = "Application", Status = VerificationStatus.Pass },
            new() { Verifier = "NonEmptyChange", Status = VerificationStatus.Pass },
            new() { Verifier = "Build", Status = VerificationStatus.Fail, Message = "Build failed" },
            new() { Verifier = "Tests", Status = VerificationStatus.Pass },
            new() { Verifier = "Scope", Status = VerificationStatus.Pass },
            new() { Verifier = "Budget", Status = VerificationStatus.Pass }
        };

        var contract = new TaskContract
        {
            Verification = new VerificationProfile { Build = true, UnitTests = true, Scope = true, Budget = true },
            Approval = new ApprovalPolicy { Production = ApprovalLevel.None }
        };

        var decision = _decisionEngine.Decide(results, contract);

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().Contain(f => f.Contains("Build"));
    }

    [Fact]
    public void HistoryCommand_TracksExecutions()
    {
        var history = new List<string>();

        history.Add("TASK-001: VERIFIED");
        history.Add("TASK-002: REJECTED");

        history.Should().HaveCount(2);
        history.Should().Contain("TASK-001: VERIFIED");
    }

    [Fact]
    public void GuideCommand_DescribesCompleteTaskReviewPromotionFlow()
    {
        var text = JarvisRepl.GuidedFlowText(Environment.CurrentDirectory);

        text.Should().Contain("AECS GUIDED FLOW");
        text.Should().Contain("aecs doctor --repo <repo> --mock");
        text.Should().Contain("aecs> run <task-file>");
        text.Should().Contain("aecs> explain --evidence <evidence-id>");
        text.Should().Contain("aecs> review <evidence-id> --policy <policy-ref>");
        text.Should().Contain("aecs> export-patch <evidence-id> <outside-repo.patch>");
        text.Should().Contain("PROMOTE <diff-hash>");
    }

    [Fact]
    public void GuideCommand_DocumentsTrustBoundariesAndOutcomeLanguage()
    {
        var text = JarvisRepl.GuidedFlowText(Environment.CurrentDirectory);

        text.Should().Contain("no agent is called");
        text.Should().Contain("never presented as final success");
        text.Should().Contain("Approval never grants extra agent permissions");
        text.Should().Contain("code rejected");
        text.Should().Contain("infrastructure failed");
        text.Should().Contain("cancelled/interrupted");
        text.Should().Contain("human review pending");
    }
}
