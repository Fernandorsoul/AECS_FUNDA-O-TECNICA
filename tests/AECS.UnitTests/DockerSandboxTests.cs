using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Sandbox;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class DockerSandboxTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        $"aecs-staging-sandbox-unit-{Guid.NewGuid():N}");

    public DockerSandboxTests()
    {
        Directory.CreateDirectory(_workspace);
    }

    [Fact]
    public async Task StructuredRequest_UsesHardenedDockerArgumentsAndRecordsEnvironment()
    {
        var dockerCli = new RecordingRunner(
            new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "9.0.100",
                Duration = TimeSpan.FromMilliseconds(25)
            },
            new ProcessExecutionResult { ExitCode = 1 });
        var sandbox = new SandboxExecutionProfile
        {
            CpuLimit = "0.75",
            MemoryLimit = "384m",
            ProcessLimit = 41,
            WallClockSeconds = 17
        };
        var runner = new DockerSandboxProcessRunner(
            dockerCli,
            _workspace,
            sandbox,
            "29.1.2",
            "1001:1001");

        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "dotnet",
            Arguments = ["build", "Fixture.csproj"],
            WorkingDirectory = _workspace,
            Timeout = TimeSpan.FromSeconds(30)
        }, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        dockerCli.Requests.Should().HaveCount(2);
        var run = dockerCli.Requests[0];
        run.FileName.Should().Be("docker");
        run.Timeout.Should().Be(TimeSpan.FromSeconds(17));
        run.Arguments.Should().ContainInOrder(
            "run", "--rm", "--pull=missing", "--name");
        run.Arguments.Should().ContainInOrder("--network", "none");
        run.Arguments.Should().ContainInOrder("--cpus", "0.75");
        run.Arguments.Should().ContainInOrder("--memory", "384m");
        run.Arguments.Should().ContainInOrder("--pids-limit", "41");
        run.Arguments.Should().Contain("--read-only");
        run.Arguments.Should().Contain("ALL");
        run.Arguments.Should().NotContain("/bin/sh");
        run.Arguments.Should().NotContain("-c");
        run.Arguments.Should().ContainInOrder(
            sandbox.Image, "dotnet", "build", "Fixture.csproj");
        dockerCli.Requests[1].Arguments.Should().ContainInOrder("rm", "--force");

        result.Environment.Should().BeEquivalentTo(new ExecutionEnvironmentEvidence
        {
            Runtime = "docker",
            RuntimeVersion = "29.1.2",
            Image = "mcr.microsoft.com/dotnet/sdk:9.0",
            ImageDigest = SandboxExecutionProfile.DefaultImage.Split('@')[1],
            NetworkMode = "none",
            CpuLimit = "0.75",
            MemoryLimit = "384m",
            ProcessLimit = 41,
            WallClockLimitSeconds = 17,
            WorkspaceMount = "/workspace:rw",
            DevelopmentHostOverride = false
        });
    }

    [Fact]
    public async Task TimedOutDockerClient_StillForcesContainerCleanup()
    {
        var dockerCli = new RecordingRunner(
            new ProcessExecutionResult { ExitCode = -1, TimedOut = true },
            new ProcessExecutionResult { ExitCode = 0 });
        var runner = new DockerSandboxProcessRunner(
            dockerCli,
            _workspace,
            new SandboxExecutionProfile(),
            "29.1.2");

        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "sleep",
            Arguments = ["60"],
            WorkingDirectory = _workspace,
            Timeout = TimeSpan.FromMilliseconds(25)
        }, CancellationToken.None);

        result.TimedOut.Should().BeTrue();
        dockerCli.Requests.Should().HaveCount(2);
        var containerName = dockerCli.Requests[0].Arguments[
            dockerCli.Requests[0].Arguments.ToList().IndexOf("--name") + 1];
        dockerCli.Requests[1].Arguments.Should().Equal("rm", "--force", containerName);
        dockerCli.Requests[1].Timeout.Should().Be(TimeSpan.FromSeconds(15));
    }

    [Fact]
    public async Task RequiredDockerUnavailable_FailsClosedBeforeRepositoryCommand()
    {
        var dockerCli = new RecordingRunner(new ProcessExecutionResult
        {
            ExitCode = 1,
            StandardError = "daemon offline"
        });
        var factory = new DockerStagedProcessRunnerFactory(dockerCli);

        var action = () => factory.CreateAsync(
            _workspace,
            new RepositoryExecutionProfile { Runtime = "docker" },
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*required*daemon is unavailable*failed closed*");
        dockerCli.Requests.Should().ContainSingle();
        dockerCli.Requests[0].Arguments.Should().Equal(
            "version", "--format", "{{.Server.Version}}");
    }

    [Fact]
    public async Task ExplicitHostRuntime_IsMarkedAsDevelopmentOverride()
    {
        var host = new RecordingRunner(new ProcessExecutionResult
        {
            ExitCode = 0,
            StandardOutput = "ok"
        });
        var factory = new DockerStagedProcessRunnerFactory(host, allowHostExecution: true);
        var runner = await factory.CreateAsync(
            _workspace,
            new RepositoryExecutionProfile { Runtime = "host" },
            CancellationToken.None);

        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "dotnet",
            Arguments = ["--version"],
            WorkingDirectory = _workspace,
            Timeout = TimeSpan.FromSeconds(3)
        }, CancellationToken.None);

        host.Requests.Should().ContainSingle();
        result.Environment!.Runtime.Should().Be("host");
        result.Environment.DevelopmentHostOverride.Should().BeTrue();
        result.Environment.WorkspaceMount.Should().Be("host-direct");
    }

    [Fact]
    public async Task HostRuntimeWithoutDevelopmentOptIn_FailsBeforeCommand()
    {
        var host = new RecordingRunner();
        var factory = new DockerStagedProcessRunnerFactory(host);

        var action = () => factory.CreateAsync(
            _workspace,
            new RepositoryExecutionProfile { Runtime = "host" },
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*override is disabled*--allow-host-execution*");
        host.Requests.Should().BeEmpty();
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private sealed class RecordingRunner : IProcessRunner
    {
        private readonly Queue<ProcessExecutionResult> _results;

        public RecordingRunner(params ProcessExecutionResult[] results)
        {
            _results = new Queue<ProcessExecutionResult>(results);
        }

        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }
}
