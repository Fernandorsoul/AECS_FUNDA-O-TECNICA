using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;
using AECS.Infrastructure.Sandbox;
using FluentAssertions;

namespace AECS.IntegrationTests;

[Collection(RoslynMsBuildCollection.Name)]
public sealed class DockerSandboxE2ETests
{
    private const string AdversarialSecretName = "AECS_DOCKER_E2E_SECRET";

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
                        Reference = TestSuiteVerifier.UnitName
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
                },
                TestSuites = new TestSuiteMatrix
                {
                    Unit = new TestSuiteCommandProfile
                    {
                        Mode = TestGateMode.Required,
                        Target = "tests/Unit/SandboxFixture.UnitTests.csproj",
                        TimeoutSeconds = 45
                    },
                    Integration = new TestSuiteCommandProfile
                    {
                        Mode = TestGateMode.Optional,
                        Target = "tests/Integration/SandboxFixture.IntegrationTests.csproj",
                        TimeoutSeconds = 45
                    }
                }
            },
            Verification = new VerificationProfile
            {
                Build = true,
                UnitTests = true,
                SecurityScan = true,
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

        var diagnostics = string.Join(" | ", result.BaselineVerificationResults
            .Concat(result.VerificationResults)
            .Select(item => $"{item.Verifier}:{item.Status}:{item.Message}"));
        var commandDiagnostics = string.Join(" | ", result.BaselineCommands
            .Concat(result.CandidateCommands)
            .Where(command => command.ExitCode != 0 ||
                command.Arguments.FirstOrDefault() == "list")
            .Select(command =>
                $"{command.FileName} {string.Join(' ', command.Arguments)} => " +
                $"{command.ExitCode}: stdout={command.StandardOutput}; " +
                $"stderr={command.StandardError}"));
        result.Decision.Decision.Should().Be(
            TaskDecision.Verified,
            $"the staged gates should pass; decision={result.Decision.Reason}; " +
            $"results={diagnostics}; commands={commandDiagnostics}");
        result.BaselineVerificationResults.Should().OnlyContain(item =>
            item.Status == VerificationStatus.Pass);
        result.VerificationResults.Should().Contain(item =>
            item.Verifier == "Build" && item.Status == VerificationStatus.Pass);
        result.VerificationResults.Should().Contain(item =>
            item.Verifier == TestSuiteVerifier.UnitName &&
            item.Status == VerificationStatus.Pass &&
            item.TestSuite!.Executed == 2);
        result.VerificationResults.Should().Contain(item =>
            item.Verifier == TestSuiteVerifier.IntegrationName &&
            item.Status == VerificationStatus.Pass &&
            item.TestSuite!.Executed == 1);
        result.BaselineVerificationResults.Should().Contain(item =>
            item.Verifier == "SecurityScan" &&
            item.Status == VerificationStatus.Pass &&
            item.SecurityScan != null);
        result.VerificationResults.Should().Contain(item =>
            item.Verifier == "SecurityScan" &&
            item.Status == VerificationStatus.Pass &&
            item.SecurityScan != null);
        result.AcceptanceCriteriaResults.Should().ContainSingle(item =>
            item.Status == VerificationStatus.Pass);
        result.BaselineCommands.Concat(result.CandidateCommands).Should().NotBeEmpty()
            .And.OnlyContain(command =>
                command.Environment != null &&
                command.Environment.Runtime == "docker" &&
                command.Environment.ImageDigest.StartsWith("sha256:") &&
                command.Environment.NetworkMode == "none" &&
                command.Environment.WorkspaceMount == "/workspace:ro" &&
                command.Environment.Capabilities != null &&
                command.Environment.Capabilities.PolicyVersion ==
                    ExecutionCapabilityPolicy.CurrentVersion &&
                command.Environment.Capabilities.Authority == "task-contract" &&
                !command.Environment.DevelopmentHostOverride);
        result.BaselineCommands.Concat(result.CandidateCommands).Should().Contain(command =>
            command.Arguments.FirstOrDefault() == "list" &&
            command.Environment!.Capabilities!.Phase.EndsWith(
                ".security-scan",
                StringComparison.Ordinal));
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
        var previousSecret = Environment.GetEnvironmentVariable(AdversarialSecretName);
        var secretValue = $"e2e-secret-{Guid.NewGuid():N}";
        Environment.SetEnvironmentVariable(AdversarialSecretName, secretValue);

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
                },
                Capabilities = AdversarialTestCapabilities()
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
                [".aecs-verification/sandbox-created.txt"]);
            write.Succeeded.Should().BeTrue(write.StandardError);
            File.Exists(Path.Combine(
                workspace,
                ".aecs-verification",
                "sandbox-created.txt")).Should().BeTrue();

            var forbiddenWrite = await RunAsync(
                runner,
                workspace,
                "touch",
                ["forbidden.txt"]);
            forbiddenWrite.Succeeded.Should().BeFalse();
            File.Exists(Path.Combine(workspace, "forbidden.txt")).Should().BeFalse();

            var forbiddenProcess = await RunAsync(
                runner,
                workspace,
                "sh",
                ["-c", "cat /etc/passwd"]);
            forbiddenProcess.Succeeded.Should().BeFalse();
            forbiddenProcess.Environment!.Capabilities!.Denied.Should().ContainSingle();

            var secretAttempt = await runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "printenv",
                Arguments = [AdversarialSecretName],
                WorkingDirectory = workspace,
                Timeout = TimeSpan.FromSeconds(10),
                Phase = ExecutionCapabilityPhases.CandidateAcceptance
            }, CancellationToken.None);
            secretAttempt.Succeeded.Should().BeTrue(secretAttempt.StandardError);
            secretAttempt.StandardOutput.Should().Be(
                "[REDACTED: output suppressed for a secret-bearing phase]");
            secretAttempt.Environment!.Capabilities!.InjectedSecrets.Should()
                .Equal(AdversarialSecretName);
            System.Text.Json.JsonSerializer.Serialize(secretAttempt).Should()
                .NotContain(secretValue);

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

            var failure = await RunAsync(
                runner,
                workspace,
                "ls",
                ["definitely-missing"]);
            failure.Succeeded.Should().BeFalse();

            var symlink = Path.Combine(workspace, ".aecs-verification", "escape");
            try
            {
                Directory.CreateSymbolicLink(symlink, original);
            }
            catch (Exception exception) when (
                OperatingSystem.IsWindows() &&
                exception is IOException or UnauthorizedAccessException)
            {
                var junction = await host.RunAsync(new ProcessExecutionRequest
                {
                    FileName = "cmd.exe",
                    Arguments = ["/d", "/c", "mklink", "/J", symlink, original],
                    WorkingDirectory = workspace,
                    Timeout = TimeSpan.FromSeconds(10)
                }, CancellationToken.None);
                junction.Succeeded.Should().BeTrue(junction.StandardError);
            }
            var symlinkAttempt = await RunAsync(
                runner,
                workspace,
                "touch",
                [".aecs-verification/escape/exfiltrated.txt"]);
            symlinkAttempt.Succeeded.Should().BeFalse();
            symlinkAttempt.Environment!.Capabilities!.Denied.Should().ContainSingle(item =>
                item.Contains("symbolic link", StringComparison.Ordinal));
            File.Exists(Path.Combine(original, "exfiltrated.txt")).Should().BeFalse();
            Directory.Delete(symlink);

            var timeout = await runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "sleep",
                Arguments = ["30"],
                WorkingDirectory = workspace,
                Timeout = TimeSpan.FromSeconds(1),
                Phase = ExecutionCapabilityPhases.CandidateTest
            }, CancellationToken.None);
            timeout.TimedOut.Should().BeTrue();

            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(1));
            var cancelled = await runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "sleep",
                Arguments = ["30"],
                WorkingDirectory = workspace,
                Timeout = TimeSpan.FromSeconds(10),
                Phase = ExecutionCapabilityPhases.CandidateTest
            }, cancellation.Token);
            cancelled.Cancelled.Should().BeTrue();

            (await SandboxContainersAsync(host, workspace)).Should().BeEmpty();
        }
        finally
        {
            Environment.SetEnvironmentVariable(AdversarialSecretName, previousSecret);
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
            Timeout = TimeSpan.FromSeconds(10),
            Phase = ExecutionCapabilityPhases.CandidateTest
        }, CancellationToken.None);

    private static ExecutionCapabilityPolicy AdversarialTestCapabilities() => new()
    {
        FileSystem = new FileSystemCapabilities
        {
            Read = ["**"],
            Write = [".aecs-verification/**"]
        },
        Processes =
        [
            Rule("find", ["/"]),
            Rule("touch", [".aecs-verification/sandbox-created.txt"]),
            Rule("touch", ["forbidden.txt"]),
            Rule("touch", [".aecs-verification/escape/exfiltrated.txt"]),
            Rule("getent", ["hosts"]),
            Rule("cat", ["/sys/fs/cgroup/memory.max"]),
            Rule("cat", ["/sys/fs/cgroup/pids.max"]),
            Rule("cat", ["/sys/fs/cgroup/cpu.max"]),
            Rule("ls", ["definitely-missing"]),
            Rule("sleep", ["30"]),
            new ProcessCapabilityRule
            {
                Executable = "printenv",
                ArgumentPrefix = [AdversarialSecretName],
                Phases = [ExecutionCapabilityPhases.CandidateAcceptance]
            }
        ],
        Secrets =
        [
            new SecretCapability
            {
                Name = AdversarialSecretName,
                Phases = [ExecutionCapabilityPhases.CandidateAcceptance]
            }
        ],
        Resources = new ResourceCapabilities
        {
            CpuLimit = "0.50",
            MemoryLimit = "128m",
            ProcessLimit = 24,
            WallClockSeconds = 10
        }
    };

    private static ProcessCapabilityRule Rule(
        string executable,
        List<string> argumentPrefix) => new()
        {
            Executable = executable,
            ArgumentPrefix = argumentPrefix,
            Phases = [ExecutionCapabilityPhases.CandidateTest]
        };

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
                  <ItemGroup>
                    <ProjectReference Include="tests/Unit/SandboxFixture.UnitTests.csproj" />
                    <ProjectReference Include="tests/Integration/SandboxFixture.IntegrationTests.csproj" />
                  </ItemGroup>
                </Project>
                """);
            await WriteSyntheticTestProjectAsync(
                repository.Path,
                "tests/Unit/SandboxFixture.UnitTests.csproj",
                "unit.trx",
                total: 2);
            await WriteSyntheticTestProjectAsync(
                repository.Path,
                "tests/Integration/SandboxFixture.IntegrationTests.csproj",
                "integration.trx",
                total: 1);
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

        private static async Task WriteSyntheticTestProjectAsync(
            string repositoryPath,
            string relativePath,
            string trxName,
            int total)
        {
            var path = System.IO.Path.Combine(repositoryPath, relativePath);
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path)!);
            await File.WriteAllTextAsync(path, $"""
                <Project Sdk="Microsoft.NET.Sdk">
                  <PropertyGroup>
                    <TargetFramework>net9.0</TargetFramework>
                  </PropertyGroup>
                  <Target Name="WriteSyntheticTrx" BeforeTargets="VSTest">
                    <MakeDir Directories="$(VSTestResultsDirectory)" />
                    <WriteLinesToFile
                      File="$(VSTestResultsDirectory)/{trxName}"
                      Lines="&lt;TestRun&gt;&lt;ResultSummary&gt;&lt;Counters total=&quot;{total}&quot; executed=&quot;{total}&quot; passed=&quot;{total}&quot; failed=&quot;0&quot; notExecuted=&quot;0&quot; /&gt;&lt;/ResultSummary&gt;&lt;/TestRun&gt;"
                      Overwrite="true" />
                  </Target>
                </Project>
                """);
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
