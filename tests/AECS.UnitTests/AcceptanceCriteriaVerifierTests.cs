using AECS.Application.Experiments;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class AcceptanceCriteriaVerifierTests
{
    [Fact]
    public async Task VerifierEvidence_PassesAndReferencesExactVerificationResult()
    {
        using var repository = new TemporaryRepository();
        var verification = Result("Scope", VerificationStatus.Pass);
        var context = Context(
            repository.Path,
            Criterion(AcceptanceEvidenceType.Verifier, "Scope"));

        var outcome = await new AcceptanceCriteriaVerifier(new FakeProcessRunner())
            .VerifyAsync(context, [verification], true, CancellationToken.None);

        outcome.AggregateResult.Status.Should().Be(VerificationStatus.Pass);
        outcome.Criteria.Should().ContainSingle();
        outcome.Criteria[0].Status.Should().Be(VerificationStatus.Pass);
        outcome.Criteria[0].EvidenceReferences.Should()
            .ContainSingle($"verification-result:{verification.Id:N}");
    }

    [Fact]
    public async Task RequiredCriterionWithoutEvidence_FailsClosed()
    {
        using var repository = new TemporaryRepository();
        var context = Context(repository.Path, Criterion(AcceptanceEvidenceType.None, ""));

        var outcome = await new AcceptanceCriteriaVerifier(new FakeProcessRunner())
            .VerifyAsync(context, [Result("Tests")], true, CancellationToken.None);

        outcome.AggregateResult.Status.Should().Be(VerificationStatus.Fail);
        outcome.Criteria[0].Status.Should().Be(VerificationStatus.Fail);
        outcome.Criteria[0].Message.Should().Contain("No executable evidence");
    }

    [Fact]
    public async Task SuccessfulTestCommandWithZeroExecutedTests_IsRejectedAsFalsePositive()
    {
        using var repository = new TemporaryRepository();
        var runner = new FakeProcessRunner { WriteTrx = false };
        var context = Context(
            repository.Path,
            Criterion(AcceptanceEvidenceType.Test, "FullyQualifiedName~MissingTest"));

        var outcome = await new AcceptanceCriteriaVerifier(runner)
            .VerifyAsync(context, [Result("Tests")], true, CancellationToken.None);

        runner.WasCalled.Should().BeTrue();
        outcome.AggregateResult.Status.Should().Be(VerificationStatus.Fail);
        outcome.Criteria[0].Message.Should().Contain("zero tests");
    }

    [Fact]
    public async Task BehavioralCriterion_WithChangedTargetedTest_PassesAndRecordsCommandEvidence()
    {
        using var repository = new TemporaryRepository();
        var runner = new FakeProcessRunner { WriteTrx = true };
        var criterion = Criterion(
            AcceptanceEvidenceType.Test,
            "FullyQualifiedName~CreateAnimal_NullName",
            behavioral: true,
            testPath: "tests/AnimalHandlerTests.cs");
        var context = Context(
            repository.Path,
            criterion,
            changedFiles: ["tests/AnimalHandlerTests.cs"]);

        var outcome = await new AcceptanceCriteriaVerifier(runner)
            .VerifyAsync(context, [Result("Tests")], true, CancellationToken.None);

        outcome.AggregateResult.Status.Should().Be(VerificationStatus.Pass);
        outcome.Criteria[0].Status.Should().Be(VerificationStatus.Pass);
        outcome.Criteria[0].Message.Should().Contain("1/1");
        outcome.Criteria[0].EvidenceReferences.Should().Contain(reference =>
            reference.StartsWith("execution-command:"));
        context.CommandEvidence.Should().ContainSingle(command =>
            command.Arguments.Contains("FullyQualifiedName~CreateAnimal_NullName"));
        Directory.Exists(Path.Combine(repository.Path, ".aecs-verification"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task BehavioralCriterion_WithoutChangedDeclaredTest_FailsBeforeExecution()
    {
        using var repository = new TemporaryRepository();
        var runner = new FakeProcessRunner { WriteTrx = true };
        var context = Context(
            repository.Path,
            Criterion(
                AcceptanceEvidenceType.Test,
                "FullyQualifiedName~BehaviorTest",
                behavioral: true,
                testPath: "tests/BehaviorTests.cs"),
            changedFiles: ["src/Behavior.cs"]);

        var outcome = await new AcceptanceCriteriaVerifier(runner)
            .VerifyAsync(context, [Result("Tests")], true, CancellationToken.None);

        runner.WasCalled.Should().BeFalse();
        outcome.AggregateResult.Status.Should().Be(VerificationStatus.Fail);
        outcome.Criteria[0].Message.Should().Contain("added or modified");
    }

    [Fact]
    public async Task BehavioralCriterion_CannotUseStructuralVerifierAsEquivalentEvidence()
    {
        using var repository = new TemporaryRepository();
        var criterion = new AcceptanceCriterion
        {
            Id = "AC-001",
            Description = "Behavior changed",
            Required = true,
            Behavioral = true,
            Evidence = new AcceptanceEvidenceRequirement
            {
                Type = AcceptanceEvidenceType.Verifier,
                Reference = "NonEmptyChange",
                EquivalentBehavioralEvidence = true
            }
        };
        var context = Context(repository.Path, criterion);

        var outcome = await new AcceptanceCriteriaVerifier(new FakeProcessRunner())
            .VerifyAsync(context, [Result("NonEmptyChange")], true, CancellationToken.None);

        outcome.AggregateResult.Status.Should().Be(VerificationStatus.Fail);
        outcome.Criteria[0].Message.Should().Contain("non-structural verifier");
    }

    [Fact]
    public void ExperimentReport_ExposesCriterionToEvidenceMatrix()
    {
        var report = new ExperimentReport
        {
            Results =
            [
                new TaskExperimentResult
                {
                    TaskId = "TASK-AC",
                    Objective = "Behavior change",
                    AcceptanceCriteria =
                    [
                        new AcceptanceCriterionResult
                        {
                            CriterionId = "AC-001",
                            Description = "Null name is rejected",
                            Required = true,
                            EvidenceType = AcceptanceEvidenceType.Test,
                            EvidenceReference = "FullyQualifiedName~NullName",
                            Status = VerificationStatus.Pass
                        }
                    ]
                }
            ]
        };

        var formatted = ExperimentReportFormatter.Format(report);

        formatted.Should().Contain("ACCEPTANCE EVIDENCE MATRIX");
        formatted.Should().Contain("TASK-AC");
        formatted.Should().Contain("AC-001");
        formatted.Should().Contain("FullyQualifiedName~NullName");
    }

    private static VerificationContext Context(
        string repositoryPath,
        AcceptanceCriterion criterion,
        List<string>? changedFiles = null) => new()
    {
        TaskId = "TASK-AC",
        AgentRunId = "RUN-AC",
        RepoPath = repositoryPath,
        Contract = new TaskContract
        {
            Id = "TASK-AC",
            AcceptanceCriteria = [criterion.Description],
            AcceptanceRequirements = [criterion],
            Budget = ExecutionBudget.Default,
            Execution = new RepositoryExecutionProfile { Target = "Fixture.csproj" },
            Verification = new VerificationProfile { Build = true, UnitTests = true }
        },
        CandidateChangeSet = new CandidateChangeSet
        {
            ModifiedFiles = changedFiles ?? ["src/Behavior.cs"],
            Diff = "diff"
        },
        CommandEvidence = []
    };

    private static AcceptanceCriterion Criterion(
        AcceptanceEvidenceType type,
        string reference,
        bool behavioral = false,
        string testPath = "") => new()
    {
        Id = "AC-001",
        Description = "Observable behavior is satisfied",
        Required = true,
        Behavioral = behavioral,
        Evidence = new AcceptanceEvidenceRequirement
        {
            Type = type,
            Reference = reference,
            TestPath = testPath
        }
    };

    private static VerificationResult Result(
        string verifier,
        VerificationStatus status = VerificationStatus.Pass) => new()
    {
        AgentRunId = "RUN-AC",
        Verifier = verifier,
        Status = status,
        Severity = status == VerificationStatus.Pass ? Severity.Info : Severity.Error,
        Message = status == VerificationStatus.Pass ? "passed" : "failed"
    };

    private sealed class FakeProcessRunner : IProcessRunner
    {
        public bool WriteTrx { get; init; }
        public bool WasCalled { get; private set; }

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            if (WriteTrx)
            {
                var resultsIndex = request.Arguments.ToList().IndexOf("--results-directory");
                var resultsDirectory = request.Arguments[resultsIndex + 1];
                Directory.CreateDirectory(resultsDirectory);
                File.WriteAllText(
                    Path.Combine(resultsDirectory, "acceptance.trx"),
                    "<TestRun><Results><UnitTestResult outcome=\"Passed\" /></Results></TestRun>");
            }

            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "Test run completed",
                Duration = TimeSpan.FromMilliseconds(10)
            });
        }
    }

    private sealed class TemporaryRepository : IDisposable
    {
        public TemporaryRepository()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aecs-acceptance-tests-{Guid.NewGuid():N}");
            Directory.CreateDirectory(Path);
            File.WriteAllText(System.IO.Path.Combine(Path, "Fixture.csproj"), "<Project />");
        }

        public string Path { get; }

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
