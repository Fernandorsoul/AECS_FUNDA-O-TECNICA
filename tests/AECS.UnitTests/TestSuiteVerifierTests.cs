using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class TestSuiteVerifierTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        $"aecs-test-suites-{Guid.NewGuid():N}");

    public TestSuiteVerifierTests()
    {
        Directory.CreateDirectory(Path.Combine(_workspace, "tests", "Unit"));
        Directory.CreateDirectory(Path.Combine(_workspace, "tests", "Integration"));
        File.WriteAllText(
            Path.Combine(_workspace, "tests", "Unit", "Unit.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        File.WriteAllText(
            Path.Combine(_workspace, "tests", "Integration", "Integration.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
    }

    [Theory]
    [InlineData(TestSuiteCategory.Unit, "tests/Unit/Unit.csproj", "baseline.unit-test")]
    [InlineData(TestSuiteCategory.Integration, "tests/Integration/Integration.csproj", "baseline.integration-test")]
    public async Task SeparateSuite_UsesStructuredTargetAndPersistsDiscovery(
        TestSuiteCategory category,
        string target,
        string expectedPhase)
    {
        var runner = new TrxRunner(executed: 3, passed: 3);
        var profile = new TestSuiteCommandProfile
        {
            Mode = TestGateMode.Required,
            Target = target,
            Arguments = ["--configuration", "Release"],
            TimeoutSeconds = 17
        };

        var result = await new TestSuiteVerifier(
            runner,
            category,
            profile,
            () => TimeSpan.FromSeconds(9)).VerifyAsync(
            Context(),
            CancellationToken.None);

        result.Status.Should().Be(VerificationStatus.Pass);
        result.Verifier.Should().Be(TestSuiteVerifier.NameFor(category));
        result.TestSuite.Should().NotBeNull();
        result.TestSuite!.Discovered.Should().Be(3);
        result.TestSuite.Executed.Should().Be(3);
        result.TestSuite.Passed.Should().Be(3);
        result.TestSuite.CommandEvidenceId.Should().NotBeEmpty();
        runner.Requests.Should().ContainSingle();
        runner.Requests[0].Arguments.Take(5).Should().Equal(
            "test", target, "--no-build", "--configuration", "Release");
        runner.Requests[0].Phase.Should().Be(expectedPhase);
        runner.Requests[0].Timeout.Should().Be(TimeSpan.FromSeconds(9));
    }

    [Fact]
    public async Task RequiredSuite_WithZeroTests_FailsClosed()
    {
        var result = await VerifyAsync(TestGateMode.Required, new TrxRunner(0, 0));

        result.Status.Should().Be(VerificationStatus.Fail);
        result.Message.Should().Contain("zero tests");
        result.TestSuite!.DiscoveryCompleted.Should().BeTrue();
        result.TestSuite.Executed.Should().Be(0);
    }

    [Fact]
    public async Task OptionalSuite_WithZeroTests_IsVisibleButNonPassing()
    {
        var result = await VerifyAsync(TestGateMode.Optional, new TrxRunner(0, 0));

        result.Status.Should().Be(VerificationStatus.Skip);
        result.TestSuite!.Mode.Should().Be(TestGateMode.Optional);
    }

    [Fact]
    public async Task MissingTrx_IsInconclusiveAndFailsRequiredSuite()
    {
        var result = await VerifyAsync(
            TestGateMode.Required,
            new TrxRunner(2, 2) { WriteTrx = false });

        result.Status.Should().Be(VerificationStatus.Fail);
        result.TestSuite!.DiscoveryCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task ExistingResults_AreRemovedBeforeDiscovery()
    {
        var staleDirectory = Path.Combine(
            _workspace,
            ".aecs-verification",
            "test-results",
            "baseline",
            "unit");
        Directory.CreateDirectory(staleDirectory);
        File.WriteAllText(
            Path.Combine(staleDirectory, "stale.trx"),
            "<TestRun><ResultSummary><Counters total=\"99\" executed=\"99\" " +
            "passed=\"99\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>");

        var result = await VerifyAsync(TestGateMode.Required, new TrxRunner(2, 2));

        result.Status.Should().Be(VerificationStatus.Pass);
        result.TestSuite!.Discovered.Should().Be(2);
        result.TestSuite.Executed.Should().Be(2);
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private Task<VerificationResult> VerifyAsync(TestGateMode mode, IProcessRunner runner) =>
        new TestSuiteVerifier(
            runner,
            TestSuiteCategory.Unit,
            new TestSuiteCommandProfile
            {
                Mode = mode,
                Target = "tests/Unit/Unit.csproj"
            }).VerifyAsync(Context(), CancellationToken.None);

    private VerificationContext Context() => new()
    {
        AgentRunId = "run",
        RepoPath = _workspace,
        Contract = new TaskContract
        {
            Budget = new ExecutionBudget { MaxDurationSeconds = 60 },
            Execution = new RepositoryExecutionProfile
            {
                WorkingDirectory = ".",
                TestSuites = new TestSuiteMatrix()
            }
        },
        CommandEvidence = [],
        Phase = "baseline"
    };

    private sealed class TrxRunner : IProcessRunner
    {
        private readonly int _executed;
        private readonly int _passed;

        public TrxRunner(int executed, int passed)
        {
            _executed = executed;
            _passed = passed;
        }

        public bool WriteTrx { get; init; } = true;
        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (WriteTrx)
            {
                var index = request.Arguments.ToList().IndexOf("--results-directory");
                var directory = request.Arguments[index + 1];
                Directory.CreateDirectory(directory);
                File.WriteAllText(
                    Path.Combine(directory, "suite.trx"),
                    $"<TestRun><ResultSummary><Counters total=\"{_executed}\" " +
                    $"executed=\"{_executed}\" passed=\"{_passed}\" " +
                    $"failed=\"{_executed - _passed}\" notExecuted=\"0\" />" +
                    "</ResultSummary></TestRun>");
            }
            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = _passed == _executed ? 0 : 1,
                Duration = TimeSpan.FromMilliseconds(25)
            });
        }
    }
}
