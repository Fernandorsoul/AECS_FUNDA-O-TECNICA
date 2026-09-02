using AECS.Application;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public class DecisionEngineTests
{
    private readonly DecisionEngine _engine = new();

    private static TaskContract CreateContract(
        ApprovalLevel approval = ApprovalLevel.None,
        bool buildRequired = true,
        bool testsRequired = true) => new()
    {
        Id = "T1",
        Objective = "Test",
        Verification = new VerificationProfile
        {
            Build = buildRequired,
            UnitTests = testsRequired
        },
        Approval = new ApprovalPolicy { Production = approval }
    };

    private static List<VerificationResult> AllRequiredPass() =>
    [
        Result("AgentSuccess"),
        Result("Application"),
        Result("NonEmptyChange"),
        Result("Build"),
        Result("Tests"),
        Result("Scope"),
        Result("Budget")
    ];

    private static VerificationResult Result(
        string verifier,
        VerificationStatus status = VerificationStatus.Pass) => new()
    {
        Verifier = verifier,
        Status = status,
        Severity = status == VerificationStatus.Pass ? Severity.Info : Severity.Error,
        Message = status == VerificationStatus.Pass ? "OK" : "Failed"
    };

    [Fact]
    public void Decide_AllRequiredPass_ReturnsVerified()
    {
        var decision = _engine.Decide(AllRequiredPass(), CreateContract());

        decision.Decision.Should().Be(TaskDecision.Verified);
        decision.TargetState.Should().Be(TaskState.Verified);
    }

    [Theory]
    [InlineData("AgentSuccess")]
    [InlineData("Application")]
    [InlineData("NonEmptyChange")]
    [InlineData("Build")]
    [InlineData("Tests")]
    [InlineData("Scope")]
    [InlineData("Budget")]
    public void Decide_RequiredVerifierFails_ReturnsRejected(string verifier)
    {
        var results = AllRequiredPass();
        var index = results.FindIndex(result => result.Verifier == verifier);
        results[index] = Result(verifier, VerificationStatus.Fail);

        var decision = _engine.Decide(results, CreateContract());

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().Contain(failure => failure.Contains(verifier));
    }

    [Fact]
    public void Decide_MissingRequiredVerifier_ReturnsRejected()
    {
        var results = AllRequiredPass();
        results.RemoveAll(result => result.Verifier == "Tests");

        var decision = _engine.Decide(results, CreateContract());

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().Contain(failure => failure.Contains("missing"));
    }

    [Theory]
    [InlineData(VerificationStatus.Skip)]
    [InlineData(VerificationStatus.Error)]
    public void Decide_SkipOrErrorRequiredVerifier_ReturnsRejected(VerificationStatus status)
    {
        var results = AllRequiredPass();
        var index = results.FindIndex(result => result.Verifier == "Build");
        results[index] = Result("Build", status);

        _engine.Decide(results, CreateContract()).Decision.Should().Be(TaskDecision.Rejected);
    }

    [Fact]
    public void Decide_EmptyResults_ReturnsRejected()
    {
        _engine.Decide([], CreateContract()).Decision.Should().Be(TaskDecision.Rejected);
    }

    [Fact]
    public void Decide_AllPass_HumanApproval_ReturnsHumanReview()
    {
        var decision = _engine.Decide(
            AllRequiredPass(),
            CreateContract(ApprovalLevel.Human));

        decision.Decision.Should().Be(TaskDecision.HumanReviewRequired);
        decision.TargetState.Should().Be(TaskState.HumanReviewRequired);
    }

    [Fact]
    public void Decide_OptionalVerifierFails_StillVerified()
    {
        var results = AllRequiredPass();
        results.Add(Result("EB002-Pattern", VerificationStatus.Fail));

        _engine.Decide(results, CreateContract()).Decision.Should().Be(TaskDecision.Verified);
    }

    [Fact]
    public void Decide_TextualAcceptanceWithoutAggregateEvidence_IsRejected()
    {
        var contract = new TaskContract
        {
            Id = "T-AC",
            Objective = "Behavior change",
            AcceptanceCriteria = ["Observable behavior"],
            Verification = new VerificationProfile { Build = true, UnitTests = true }
        };

        var decision = _engine.Decide(AllRequiredPass(), contract);

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().Contain(failure =>
            failure.Contains("AcceptanceCriteria") && failure.Contains("missing"));
    }

    [Fact]
    public void Decide_AcceptanceAggregatePass_IsVerified()
    {
        var contract = new TaskContract
        {
            Id = "T-AC",
            Objective = "Behavior change",
            AcceptanceCriteria = ["Observable behavior"],
            Verification = new VerificationProfile { Build = true, UnitTests = true }
        };
        var results = AllRequiredPass();
        results.Add(Result("AcceptanceCriteria"));

        _engine.Decide(results, contract).Decision.Should().Be(TaskDecision.Verified);
    }

    [Fact]
    public void Decide_CriticalSemanticFailure_IsBlockedByDefaultPolicy()
    {
        var results = AllRequiredPass();
        results.Add(new VerificationResult
        {
            Verifier = "EB003-BreakingChange",
            Status = VerificationStatus.Fail,
            Severity = Severity.Critical,
            Message = "Critical contract break"
        });

        var decision = _engine.Decide(results, CreateContract());

        decision.Decision.Should().Be(TaskDecision.Rejected);
        decision.Failures.Should().Contain(failure =>
            failure.Contains("critical semantic policy"));
    }

    [Fact]
    public void Decide_CriticalSemanticFailure_CanBeExplicitlyNonBlocking()
    {
        var contract = new TaskContract
        {
            Id = "T1",
            Objective = "Test",
            Verification = new VerificationProfile
            {
                Build = true,
                UnitTests = true,
                BlockCriticalSemanticFailures = false
            }
        };
        var results = AllRequiredPass();
        results.Add(new VerificationResult
        {
            Verifier = "EB003-BreakingChange",
            Status = VerificationStatus.Fail,
            Severity = Severity.Critical,
            Message = "Explicitly advisory"
        });

        _engine.Decide(results, contract).Decision.Should().Be(TaskDecision.Verified);
    }

    [Fact]
    public void Decide_VersionedSuites_BlockOnlyRequiredCategory()
    {
        var contract = new TaskContract
        {
            Verification = new VerificationProfile { Build = false },
            Execution = new RepositoryExecutionProfile
            {
                TestSuites = new TestSuiteMatrix
                {
                    Unit = new TestSuiteCommandProfile
                    {
                        Mode = TestGateMode.Required,
                        Target = "tests/Unit/Unit.csproj"
                    },
                    Integration = new TestSuiteCommandProfile
                    {
                        Mode = TestGateMode.Optional,
                        Target = "tests/Integration/Integration.csproj"
                    }
                }
            }
        };
        var results = new List<VerificationResult>
        {
            Result("AgentSuccess"), Result("Application"), Result("NonEmptyChange"),
            Result("Scope"), Result("Budget"), Result(TestSuiteVerifier.UnitName),
            Result(TestSuiteVerifier.IntegrationName, VerificationStatus.Fail)
        };

        _engine.Decide(results, contract).Decision.Should().Be(TaskDecision.Verified);

        results.RemoveAll(result => result.Verifier == TestSuiteVerifier.UnitName);
        var rejected = _engine.Decide(results, contract);
        rejected.Decision.Should().Be(TaskDecision.Rejected);
        rejected.Failures.Should().Contain(failure => failure.Contains(TestSuiteVerifier.UnitName));
    }
}
