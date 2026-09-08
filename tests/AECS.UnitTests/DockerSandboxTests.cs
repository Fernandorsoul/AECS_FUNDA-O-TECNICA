using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Sandbox;
using FluentAssertions;
using System.Text.Json;

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
        var legacyCapabilities = ExecutionCapabilityPolicy.LegacyCompatibility(false);
        var dockerCli = new RecordingRunner(
            new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = "10.0.400",
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
            Image = "mcr.microsoft.com/dotnet/sdk:10.0",
            ImageDigest = SandboxExecutionProfile.DefaultImage.Split('@')[1],
            NetworkMode = "none",
            CpuLimit = "0.75",
            MemoryLimit = "384m",
            ProcessLimit = 41,
            WallClockLimitSeconds = 17,
            WorkspaceMount = "/workspace:rw",
            DevelopmentHostOverride = false,
            Capabilities = new ExecutionCapabilityEvidence
            {
                PolicyVersion = legacyCapabilities.Version,
                Authority = legacyCapabilities.Authority,
                PolicyHash = ExecutionCapabilityPolicyFingerprint.Create(legacyCapabilities),
                Granted = ["legacy:authenticated-compatibility"]
            }
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

    [Fact]
    public void DirectRunnerCannotMountAnOriginalCheckout()
    {
        var original = Path.Combine(
            Path.GetTempPath(),
            $"aecs-original-sandbox-unit-{Guid.NewGuid():N}");
        Directory.CreateDirectory(original);
        try
        {
            var action = () => new DockerSandboxProcessRunner(
                new RecordingRunner(),
                original,
                new SandboxExecutionProfile(),
                "29.1.2");

            action.Should().Throw<InvalidOperationException>()
                .WithMessage("*only an AECS disposable staged workspace*");
        }
        finally
        {
            Directory.Delete(original);
        }
    }

    [Fact]
    public async Task RestrictiveCapabilities_MountWorkspaceReadOnlyAndOnlyBuildOutputsWritable()
    {
        await File.WriteAllTextAsync(
            Path.Combine(_workspace, "Fixture.csproj"),
            "<Project Sdk=\"Microsoft.NET.Sdk\" />");
        var dockerCli = new RecordingRunner(
            new ProcessExecutionResult { ExitCode = 0 },
            new ProcessExecutionResult { ExitCode = 1 });
        var policy = ExecutionCapabilityPolicy.RestrictiveDefault();
        var runner = new DockerSandboxProcessRunner(
            dockerCli,
            _workspace,
            new SandboxExecutionProfile(),
            "29.1.2",
            capabilities: policy);

        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "dotnet",
            Arguments = ["build", "Fixture.csproj"],
            WorkingDirectory = _workspace,
            Timeout = TimeSpan.FromSeconds(10),
            Phase = ExecutionCapabilityPhases.BaselineBuild
        }, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        var runArguments = dockerCli.Requests[0].Arguments;
        runArguments.Should().Contain(argument =>
            argument.Contains("target=/workspace,readonly", StringComparison.Ordinal));
        runArguments.Should().Contain(argument =>
            argument.EndsWith("target=/workspace/bin", StringComparison.Ordinal));
        runArguments.Should().Contain(argument =>
            argument.EndsWith("target=/workspace/obj", StringComparison.Ordinal));
        result.Environment!.WorkspaceMount.Should().Be("/workspace:ro");
        result.Environment.Capabilities!.PolicyVersion.Should()
            .Be(ExecutionCapabilityPolicy.CurrentVersion);
        result.Environment.Capabilities.Granted.Should().Contain(
            "filesystem:write:**/bin/**");
    }

    [Fact]
    public async Task UnauthorizedProcessOrArguments_AreDeniedBeforeDockerAndRecorded()
    {
        var dockerCli = new RecordingRunner();
        var runner = new DockerSandboxProcessRunner(
            dockerCli,
            _workspace,
            new SandboxExecutionProfile(),
            "29.1.2",
            capabilities: ExecutionCapabilityPolicy.RestrictiveDefault());

        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "sh",
            Arguments = ["-c", "cat /etc/passwd"],
            WorkingDirectory = _workspace,
            Timeout = TimeSpan.FromSeconds(10),
            Phase = ExecutionCapabilityPhases.CandidateBuild
        }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        dockerCli.Requests.Should().BeEmpty();
        result.StandardError.Should().NotContain("/etc/passwd");
        result.Environment!.Capabilities!.Denied.Should().ContainSingle()
            .Which.Should().Contain("process:sh");
        result.Environment.Capabilities.Authority.Should().Be("task-contract");
    }

    [Fact]
    public async Task SecretBearingPhase_ForwardsNameOnlyAndSuppressesAllOutput()
    {
        const string secretName = "AECS_CAPABILITY_TEST_SECRET";
        var previous = Environment.GetEnvironmentVariable(secretName);
        var secretValue = $"secret-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(secretName, secretValue);
        try
        {
            var policy = Policy(
                processes:
                [
                    new ProcessCapabilityRule
                    {
                        Executable = "printenv",
                        ArgumentPrefix = [secretName],
                        Phases = [ExecutionCapabilityPhases.CandidateTest]
                    }
                ],
                secrets:
                [
                    new SecretCapability
                    {
                        Name = secretName,
                        Phases = [ExecutionCapabilityPhases.CandidateTest]
                    }
                ]);
            var dockerCli = new RecordingRunner(
                new ProcessExecutionResult
                {
                    ExitCode = 0,
                    StandardOutput = secretValue,
                    StandardError = Convert.ToBase64String(
                        System.Text.Encoding.UTF8.GetBytes(secretValue))
                },
                new ProcessExecutionResult { ExitCode = 1 });
            var runner = new DockerSandboxProcessRunner(
                dockerCli,
                _workspace,
                new SandboxExecutionProfile(),
                "29.1.2",
                capabilities: policy);

            var result = await runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "printenv",
                Arguments = [secretName],
                WorkingDirectory = _workspace,
                Timeout = TimeSpan.FromSeconds(10),
                Phase = ExecutionCapabilityPhases.CandidateTest
            }, CancellationToken.None);

            result.StandardOutput.Should().Be("[REDACTED: output suppressed for a secret-bearing phase]");
            result.StandardError.Should().Be("[REDACTED: error output suppressed for a secret-bearing phase]");
            dockerCli.Requests[0].Arguments.Should().ContainInOrder("--env", secretName);
            dockerCli.Requests[0].Arguments.Should().NotContain(secretValue);
            result.Environment!.Capabilities!.InjectedSecrets.Should().Equal(secretName);
            JsonSerializer.Serialize(result).Should().NotContain(secretValue);
        }
        finally
        {
            Environment.SetEnvironmentVariable(secretName, previous);
        }
    }

    [Fact]
    public async Task NetworkRequiresBothAuthorizedPhaseAndExplicitWildcardDestination()
    {
        var policy = Policy(
            processes:
            [
                new ProcessCapabilityRule
                {
                    Executable = "getent",
                    ArgumentPrefix = ["hosts"],
                    Phases = [ExecutionCapabilityPhases.CandidateTest]
                }
            ],
            network: new NetworkCapabilities
            {
                Destinations = ["*"],
                Phases = [ExecutionCapabilityPhases.CandidateTest]
            });
        var dockerCli = new RecordingRunner(
            new ProcessExecutionResult { ExitCode = 0 },
            new ProcessExecutionResult { ExitCode = 1 });
        var runner = new DockerSandboxProcessRunner(
            dockerCli,
            _workspace,
            new SandboxExecutionProfile { NetworkAccess = true },
            "29.1.2",
            capabilities: policy);

        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "getent",
            Arguments = ["hosts", "example.com"],
            WorkingDirectory = _workspace,
            Timeout = TimeSpan.FromSeconds(10),
            Phase = ExecutionCapabilityPhases.CandidateTest
        }, CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        dockerCli.Requests[0].Arguments.Should().ContainInOrder("--network", "bridge");
        result.Environment!.Capabilities!.Granted.Should()
            .Contain("network:destination:*");
    }

    [Theory]
    [InlineData("candidate.build", "*", "network:phase-not-authorized")]
    [InlineData("candidate.test", "example.com", "destination-scoped-egress-unavailable")]
    public async Task NetworkWithoutEnforceablePhaseAndDestination_IsDeniedBeforeDocker(
        string allowedPhase,
        string destination,
        string expectedDenial)
    {
        var policy = Policy(
            processes:
            [
                new ProcessCapabilityRule
                {
                    Executable = "getent",
                    ArgumentPrefix = ["hosts"],
                    Phases = [ExecutionCapabilityPhases.CandidateTest]
                }
            ],
            network: new NetworkCapabilities
            {
                Destinations = [destination],
                Phases = [allowedPhase]
            });
        var dockerCli = new RecordingRunner();
        var runner = new DockerSandboxProcessRunner(
            dockerCli,
            _workspace,
            new SandboxExecutionProfile { NetworkAccess = true },
            "29.1.2",
            capabilities: policy);

        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "getent",
            Arguments = ["hosts", "example.com"],
            WorkingDirectory = _workspace,
            Timeout = TimeSpan.FromSeconds(10),
            Phase = ExecutionCapabilityPhases.CandidateTest
        }, CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        dockerCli.Requests.Should().BeEmpty();
        result.Environment!.Capabilities!.Denied.Should().ContainSingle()
            .Which.Should().Contain(expectedDenial);
    }

    [Fact]
    public async Task WritableCapabilityContainingSymlink_IsDeniedBeforeDocker()
    {
        if (OperatingSystem.IsWindows())
            return;

        var outside = Path.Combine(Path.GetTempPath(), $"aecs-cap-outside-{Guid.NewGuid():N}");
        Directory.CreateDirectory(outside);
        var artifacts = Path.Combine(_workspace, "artifacts");
        Directory.CreateDirectory(artifacts);
        Directory.CreateSymbolicLink(Path.Combine(artifacts, "escape"), outside);
        try
        {
            var policy = Policy(
                write: ["artifacts/**"],
                processes:
                [
                    new ProcessCapabilityRule
                    {
                        Executable = "touch",
                        ArgumentPrefix = ["artifacts/output.txt"],
                        Phases = [ExecutionCapabilityPhases.CandidateTest]
                    }
                ]);
            var dockerCli = new RecordingRunner();
            var runner = new DockerSandboxProcessRunner(
                dockerCli,
                _workspace,
                new SandboxExecutionProfile(),
                "29.1.2",
                capabilities: policy);

            var result = await runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "touch",
                Arguments = ["artifacts/output.txt"],
                WorkingDirectory = _workspace,
                Timeout = TimeSpan.FromSeconds(10),
                Phase = ExecutionCapabilityPhases.CandidateTest
            }, CancellationToken.None);

            dockerCli.Requests.Should().BeEmpty();
            result.Environment!.Capabilities!.Denied.Should().ContainSingle()
                .Which.Should().Contain("symbolic link");
        }
        finally
        {
            Directory.Delete(outside, recursive: true);
        }
    }

    public void Dispose()
    {
        if (Directory.Exists(_workspace))
            Directory.Delete(_workspace, recursive: true);
    }

    private static ExecutionCapabilityPolicy Policy(
        List<string>? write = null,
        List<ProcessCapabilityRule>? processes = null,
        NetworkCapabilities? network = null,
        List<SecretCapability>? secrets = null) => new()
        {
            FileSystem = new FileSystemCapabilities
            {
                Read = ["**"],
                Write = write ?? []
            },
            Processes = processes ?? [],
            Network = network ?? new NetworkCapabilities(),
            Secrets = secrets ?? [],
            Resources = new ResourceCapabilities()
        };

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
