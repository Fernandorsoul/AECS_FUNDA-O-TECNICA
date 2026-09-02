using AECS.Application.Verification;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class SharedWallClockVerifierTests
{
    [Fact]
    public async Task BuildAndTests_UseCurrentSharedWallClockRemainder()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            $"aecs-shared-budget-{Guid.NewGuid():N}");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(
            Path.Combine(root, "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");

        try
        {
            var runner = new RecordingProcessRunner();
            var remaining = TimeSpan.FromSeconds(5);
            Func<TimeSpan> getRemaining = () => remaining;
            var context = Context(root);

            var build = await new BuildVerifier(runner, getRemaining).VerifyAsync(
                context,
                CancellationToken.None);
            remaining = TimeSpan.FromSeconds(2);
            var tests = await new TestVerifier(runner, getRemaining).VerifyAsync(
                context,
                CancellationToken.None);

            build.Status.Should().Be(AECS.Domain.Enums.VerificationStatus.Pass);
            tests.Status.Should().Be(AECS.Domain.Enums.VerificationStatus.Pass);
            runner.Requests.Should().HaveCount(2);
            runner.Requests[0].Timeout.Should().Be(TimeSpan.FromSeconds(5));
            runner.Requests[1].Timeout.Should().Be(TimeSpan.FromSeconds(2));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task ExhaustedWallClock_BlocksCommandBeforeProcessStarts()
    {
        var runner = new RecordingProcessRunner();
        var context = Context(Path.GetTempPath());

        var result = await new BuildVerifier(
                runner,
                () => TimeSpan.Zero)
            .VerifyAsync(context, CancellationToken.None);

        result.Status.Should().Be(AECS.Domain.Enums.VerificationStatus.Error);
        result.Message.Should().Contain("Wall-clock budget exhausted");
        runner.Requests.Should().BeEmpty();
    }

    private static VerificationContext Context(string root) => new()
    {
        TaskId = "T-WALL",
        AgentRunId = "R-WALL",
        RepoPath = root,
        Contract = new TaskContract
        {
            Budget = new ExecutionBudget
            {
                MaxTokens = 1000,
                MaxCostUsd = 1m,
                MaxRetries = 1,
                MaxDurationSeconds = 60,
                MaxFilesChanged = 10
            },
            Execution = new RepositoryExecutionProfile { Target = "Fixture.csproj" }
        }
    };

    private sealed class RecordingProcessRunner : IProcessRunner
    {
        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(new ProcessExecutionResult { ExitCode = 0 });
        }
    }
}
