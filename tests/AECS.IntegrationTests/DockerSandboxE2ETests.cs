using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;
using AECS.Infrastructure.Sandbox;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class DockerSandboxE2ETests
{
    private static bool Enabled => string.Equals(
        Environment.GetEnvironmentVariable("AECS_RUN_DOCKER_E2E"),
        "1",
        StringComparison.Ordinal);

    [Fact]
    [Trait("Category", "DockerSandbox")]
    public async Task Pipeline_BuildTestsAndAcceptance_RunInsidePinnedSandbox()
    {
        if (!Enabled)
            return;

        await using var repository = await DockerFixtureRepository.CreateAsync();
        var runner = repository.ProcessRunner;
        var contract = new TaskContract
        {
            Id = "DOCKER-E2E-PIPELINE",
            Objective = "Change the staged console output",
            AcceptanceCriteria = ["The sandboxed test gate passes"],
            AcceptanceRequirements =
            [
                new AcceptanceCriterion
                {
                    Id = "AC-001",
                    Description = "The sandboxed test gate passes",
                    Evidence = new AcceptanceEvidenceRequirement
                    {
                        Type = AcceptanceEvidenceType.Verifier,
                        Reference = "Tests"
                    }
                }
            ],
            Scope = new ScopeDefinition { Allowed = ["Program.cs"] },
            Budget = new ExecutionBudget
            {
                MaxTokens = 1000,
                MaxCostUsd = 1m,
                MaxRetries = 0,
                MaxDurationSeconds = 240,
                MaxFilesChanged = 1
            },
            Execution = new RepositoryExecutionProfile
            {
                Runtime = RepositoryExecutionProfile.DockerRuntime,
                Target = "SandboxFixture.csproj",
                Sandbox = new SandboxExecutionProfile
                {
                    CpuLimit = "0.75",
                    MemoryLimit = "384m",
                    ProcessLimit = 64,
                    WallClockSeconds = 90,
                    NetworkAccess = false
                }
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
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var result = await new StagedExecutionPipeline(
                new FixtureAgent("""
                    FILE: Program.cs
                    ```csharp
                    Console.WriteLine("candidate");
                    ```
                    """),
                runner,
                store,
                stagedProcessRunnerFactory: new DockerStagedProcessRunnerFactory(runner))
            .RunAsync(repository.Path, contract, CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Verified);
        result.BaselineVerificationResults.Should().OnlyContain(item =>
            item.Status == VerificationStatus.Pass);
        result.VerificationResults.Should().Contain(item =>
            item.Verifier == "Build" && item.Status == VerificationStatus.Pass);
        result.VerificationResults.Should().Contain(item =>
            item.Verifier == "Tests" && item.Status == VerificationStatus.Pass);
        result.AcceptanceCriteriaResults.Should().ContainSingle(item =>
            item.Status == VerificationStatus.Pass);
        result.BaselineCommands.Concat(result.CandidateCommands).Should().NotBeEmpty()
            .And.OnlyContain(command =>
                command.Environment != null &&
                command.Environment.Runtime == "docker" &&
                command.Environment.ImageDigest.StartsWith("sha256:") &&
                command.Environment.NetworkMode == "none" &&
                !command.Environment.DevelopmentHostOverride);
        (await repository.StatusAsync()).Should().BeEmpty();
        (await SandboxContainersAsync(runner, repository.Path)).Should().BeEmpty();
    }

    [Fact]
    [Trait("Category", "DockerSandbox")]
    public async Task RealSandbox_IsolatesFilesystemNetworkResourcesAndAlwaysCleansUp()
    {
        if (!Enabled)
            return;

        var root = Path.Combine(Path.GetTempPath(), $"aecs-docker-real-{Guid.NewGuid():N}");
        var original = Path.Combine(root, "original-checkout");
        var workspace = Path.Combine(root, $"aecs-staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(original);
        Directory.CreateDirectory(workspace);
        var marker = $"original-only-{Guid.NewGuid():N}.secret";
        await File.WriteAllTextAsync(Path.Combine(original, marker), "must not be mounted");
        var host = new SystemProcessRunner();

        try
        {
            var profile = new RepositoryExecutionProfile
            {
                Runtime = RepositoryExecutionProfile.DockerRuntime,
                Sandbox = new SandboxExecutionProfile
                {
                    CpuLimit = "0.50",
                    MemoryLimit = "128m",
                    ProcessLimit = 24,
                    WallClockSeconds = 10,
                    NetworkAccess = false
                }
            };
            var runner = await new DockerStagedProcessRunnerFactory(host).CreateAsync(
                workspace,
                profile,
                CancellationToken.None);

            var filesystem = await RunAsync(
                runner,
                workspace,
                "find",
                ["/", "-name", marker, "-print"]);
            filesystem.StandardOutput.Should().NotContain(marker);

            var write = await RunAsync(
                runner,
                workspace,
                "touch",
                ["sandbox-created.txt"]);
            write.Succeeded.Should().BeTrue(write.StandardError);
            File.Exists(Path.Combine(workspace, "sandbox-created.txt")).Should().BeTrue();

            var network = await RunAsync(
                runner,
                workspace,
                "getent",
                ["hosts", "example.com"]);
            network.Succeeded.Should().BeFalse();
            network.Environment!.NetworkMode.Should().Be("none");

            var memory = await RunAsync(
                runner,
                workspace,
                "cat",
                ["/sys/fs/cgroup/memory.max"]);
            long.Parse(memory.StandardOutput.Trim()).Should().BeLessThanOrEqualTo(128L * 1024 * 1024);

            var processes = await RunAsync(
                runner,
                workspace,
                "cat",
                ["/sys/fs/cgroup/pids.max"]);
            int.Parse(processes.StandardOutput.Trim()).Should().BeLessThanOrEqualTo(24);

            var cpu = await RunAsync(
                runner,
                workspace,
                "cat",
                ["/sys/fs/cgroup/cpu.max"]);
            var cpuParts = cpu.StandardOutput.Split(
                ' ',
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            (decimal.Parse(cpuParts[0]) / decimal.Parse(cpuParts[1]))
                .Should().BeLessThanOrEqualTo(0.50m);

            var failure = await RunAsync(runner, workspace, "false", []);
            failure.Succeeded.Should().BeFalse();

            var timeout = await runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "sleep",
                Arguments = ["30"],
                WorkingDirectory = workspace,
                Timeout = TimeSpan.FromSeconds(1)
            }, CancellationToken.None);
            timeout.TimedOut.Should().BeTrue();

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var cancelled = await runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "sleep",
                Arguments = ["30"],
                WorkingDirectory = workspace,
                Timeout = TimeSpan.FromSeconds(10)
            }, cancellation.Token);
            cancelled.Cancelled.Should().BeTrue();

            (await SandboxContainersAsync(host, workspace)).Should().BeEmpty();
        }
        finally
        {
            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
        }
    }

    private static Task<ProcessExecutionResult> RunAsync(
        IProcessRunner runner,
        string workspace,
        string fileName,
        IReadOnlyList<string> arguments) => runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = fileName,
            Arguments = arguments,
            WorkingDirectory = workspace,
            Timeout = TimeSpan.FromSeconds(10)
        }, CancellationToken.None);

    private static async Task<string> SandboxContainersAsync(
        IProcessRunner runner,
        string workingDirectory)
    {
        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "docker",
            Arguments =
            [
                "ps", "--all", "--filter", "label=aecs.sandbox=true",
                "--format", "{{.Names}}"
            ],
            WorkingDirectory = workingDirectory,
            Timeout = TimeSpan.FromSeconds(15)
        }, CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.StandardError);
        return result.StandardOutput.Trim();
    }

    private sealed class FixtureAgent : IAgentAdapter
    {
        private readonly string _output;

        public FixtureAgent(string output)
        {
            _output = output;
        }

        public Task<AgentRunResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken) => Task.FromResult(new AgentRunResult
            {
                Success = true,
                StdOut = _output,
                ExitCode = 0,
                ExitReason = "Completed",
                InputTokens = 10,
                OutputTokens = 10,
                EstimatedCost = 0.001m
            });
    }

    private sealed class DockerFixtureRepository : IAsyncDisposable
    {
        private DockerFixtureRepository(string root)
        {
            Root = root;
            Path = System.IO.Path.Combine(root, "repository");
            EvidencePath = System.IO.Path.Combine(root, "evidence");
        }

        public string Root { get; }
        public string Path { get; }
        public string EvidencePath { get; }
        public SystemProcessRunner ProcessRunner { get; } = new();

        public static async Task<DockerFixtureRepository> CreateAsync()
        {
            var repository = new DockerFixtureRepository(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aecs-docker-pipeline-{Guid.NewGuid():N}"));
            Directory.CreateDirectory(repository.Path);
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(repository.Path, "SandboxFixture.csproj"),
                """
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <OutputType>Exe</OutputType>
                    <TargetFramework>net9.0</TargetFramework>
                    <ImplicitUsings>enable</ImplicitUsings>
                  </PropertyGroup>
                </Project>
                """);
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(repository.Path, "Program.cs"),
                "Console.WriteLine(\"baseline\");");
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(repository.Path, ".gitignore"),
                "bin/\nobj/\n");
            await repository.GitAsync("init", "--initial-branch=fixture");
            await repository.GitAsync("config", "user.email", "aecs-tests@example.invalid");
            await repository.GitAsync("config", "user.name", "AECS Tests");
            await repository.GitAsync("add", "-A", "--");
            await repository.GitAsync("commit", "-m", "sandbox baseline");
            return repository;
        }

        public async Task<string> StatusAsync() =>
            (await GitAsync("status", "--porcelain=v1", "--untracked-files=all"))
            .StandardOutput;

        public async ValueTask DisposeAsync()
        {
            try
            {
                await GitAsync("worktree", "prune");
            }
            catch
            {
                // The pipeline assertions detect leaks; disposal remains best effort.
            }

            if (!Directory.Exists(Root))
                return;
            foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(Root, recursive: true);
        }

        private async Task<ProcessExecutionResult> GitAsync(params string[] arguments)
        {
            var result = await ProcessRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = Path,
                Timeout = TimeSpan.FromSeconds(30)
            }, CancellationToken.None);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
            }

            return result;
        }
    }
}
