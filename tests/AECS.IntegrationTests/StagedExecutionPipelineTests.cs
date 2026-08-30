using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class StagedExecutionPipelineTests
{
    [Fact]
    public async Task ValidUntrackedChange_IsVerified_Persisted_AndOriginalRemainsUnchanged()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var agent = Agent.Success(Response("src/new-file.txt"), claimedFiles: ["claimed/not-real.txt"]);
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var pipeline = new StagedExecutionPipeline(agent, repository.ProcessRunner, store);

        var result = await pipeline.RunAsync(repository.Path, Contract(), CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Verified);
        result.CandidateChangeSet.AddedFiles.Should().ContainSingle("src/new-file.txt");
        result.CandidateChangeSet.ChangedFiles.Should().NotContain("claimed/not-real.txt");
        result.CandidateChangeSet.Diff.Should().Contain("new file mode");
        result.CandidateChangeSet.DiffHash.Should().StartWith("sha256:");
        result.OriginalRepositoryUnchanged.Should().BeTrue();
        File.Exists(System.IO.Path.Combine(repository.Path, "src", "new-file.txt")).Should().BeFalse();
        (await repository.StatusAsync()).Should().BeEmpty();

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence.Should().NotBeNull();
        evidence!.CandidateChangeSet.DiffHash.Should().Be(result.CandidateChangeSet.DiffHash);
        evidence.FinalDecision.Decision.Should().Be(TaskDecision.Verified);
        evidence.VerificationResults.Should().OnlyContain(item =>
            !evidence.FinalDecision.RequiredVerifiers.Contains(item.Verifier) ||
            item.Status == VerificationStatus.Pass);
        evidence.StateTransitions.Should().Contain(item => item.Contains("Running->CandidateProduced"));
    }

    [Fact]
    public async Task AgentReceivesCompiledContextFromDisposableWorktree_AndManifestIsPersisted()
    {
        const string handler = "namespace Fixture; public class ExistingHandler { public void Handle() { } }";
        await using var repository = await TemporaryGitRepository.CreateAsync(
            new Dictionary<string, string> { ["src/ExistingHandler.cs"] = handler });
        var agent = Agent.Success(Response("src/new-file.txt"));
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);

        var result = await new StagedExecutionPipeline(
                agent,
                repository.ProcessRunner,
                store)
            .RunAsync(repository.Path, Contract(), CancellationToken.None);

        agent.LastRequest.Should().NotBeNull();
        agent.LastRequest!.RepoPath.Should().NotBe(repository.Path);
        Directory.Exists(agent.LastRequest.RepoPath).Should().BeFalse(
            "the context source worktree must be disposed after execution");
        agent.LastRequest.CodeContext.Should().ContainKey("src/ExistingHandler.cs")
            .WhoseValue.Should().Be(handler);
        agent.LastRequest.ContextPrompt.Should().Contain("class Fixture.ExistingHandler");
        result.ContextManifest.Source.Should().Be("isolated-git-worktree");
        result.ContextManifest.BaselineCommit.Should().Be(result.Baseline.Commit);
        result.ContextManifest.Files.Should().ContainSingle(file =>
            file.Path == "src/ExistingHandler.cs" && file.Sha256.StartsWith("sha256:"));

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence.Should().NotBeNull();
        evidence!.ContextManifest.ManifestHash.Should()
            .Be(result.ContextManifest.ManifestHash);
        File.ReadAllText(System.IO.Path.Combine(repository.Path, "src", "ExistingHandler.cs"))
            .Should().Be(handler);
    }

    [Fact]
    public async Task SuccessfulAgentWithZeroDiff_IsRejected_AndOriginalRemainsUnchanged()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var result = await Pipeline(repository, Agent.Success("No file blocks"))
            .RunAsync(repository.Path, Contract(), CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.CandidateChangeSet.HasChanges.Should().BeFalse();
        result.VerificationResults.Single(item => item.Verifier == "NonEmptyChange")
            .Status.Should().Be(VerificationStatus.Fail);
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task FailedAgent_IsRejected_AndItsResponseIsNotApplied()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var agent = Agent.Failure(Response("src/should-not-exist.txt"));

        var result = await Pipeline(repository, agent)
            .RunAsync(repository.Path, Contract(), CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.CandidateChangeSet.HasChanges.Should().BeFalse();
        result.VerificationResults.Single(item => item.Verifier == "AgentSuccess")
            .Status.Should().Be(VerificationStatus.Fail);
        File.Exists(System.IO.Path.Combine(repository.Path, "src", "should-not-exist.txt"))
            .Should().BeFalse();
    }

    [Fact]
    public async Task TraversalAttempt_IsRejected_AndCannotEscapeStagingWorkspace()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var result = await Pipeline(repository, Agent.Success(Response("../escape.txt")))
            .RunAsync(repository.Path, Contract(), CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.VerificationResults.Single(item => item.Verifier == "Application")
            .Status.Should().Be(VerificationStatus.Fail);
        Directory.EnumerateFiles(repository.RootPath, "escape.txt", SearchOption.AllDirectories)
            .Should().BeEmpty();
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task FilesystemChangeOutsideScope_IsRejected_EvenWhenAgentClaimsAllowedFile()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var agent = Agent.Success(Response("docs/outside.txt"), ["src/claimed.txt"]);

        var result = await Pipeline(repository, agent)
            .RunAsync(repository.Path, Contract(), CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.CandidateChangeSet.AddedFiles.Should().Contain("docs/outside.txt");
        result.VerificationResults.Single(item => item.Verifier == "Scope")
            .Status.Should().Be(VerificationStatus.Fail);
    }

    [Fact]
    public async Task ModifiedAndDeletedFiles_AreDerivedFromGit()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync(
            new Dictionary<string, string>
            {
                ["src/modified.txt"] = "before",
                ["src/deleted.txt"] = "delete me"
            });
        var agent = Agent.Success("Changes made directly in staging", workspace =>
        {
            File.WriteAllText(System.IO.Path.Combine(workspace, "src", "modified.txt"), "after");
            File.Delete(System.IO.Path.Combine(workspace, "src", "deleted.txt"));
        });

        var result = await Pipeline(repository, agent)
            .RunAsync(repository.Path, Contract(), CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Verified);
        result.CandidateChangeSet.ModifiedFiles.Should().ContainSingle("src/modified.txt");
        result.CandidateChangeSet.DeletedFiles.Should().ContainSingle("src/deleted.txt");
        File.ReadAllText(System.IO.Path.Combine(repository.Path, "src", "modified.txt"))
            .Should().Be("before");
        File.Exists(System.IO.Path.Combine(repository.Path, "src", "deleted.txt")).Should().BeTrue();
    }

    [Fact]
    public async Task RequiredVerifierMissing_IsRejectedFailClosed()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var contract = Contract();
        contract = new TaskContract
        {
            Id = contract.Id,
            Objective = contract.Objective,
            Scope = contract.Scope,
            Budget = contract.Budget,
            Verification = new VerificationProfile
            {
                Build = false,
                UnitTests = false,
                SecurityScan = true
            },
            Approval = contract.Approval
        };

        var result = await Pipeline(repository, Agent.Success(Response("src/new.txt")))
            .RunAsync(repository.Path, contract, CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.Decision.Failures.Should().Contain(failure =>
            failure.Contains("SecurityScan") && failure.Contains("missing"));
    }

    [Fact]
    public async Task CandidateBuildFailure_IsRejectedAfterGreenBaseline()
    {
        var project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        await using var repository = await TemporaryGitRepository.CreateAsync(
            new Dictionary<string, string> { ["Fixture.csproj"] = project });
        var contract = Contract(
            build: true,
            execution: new RepositoryExecutionProfile { Target = "Fixture.csproj" });
        var agent = Agent.Success(Response("src/Broken.cs"));

        var result = await Pipeline(repository, agent)
            .RunAsync(repository.Path, contract, CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.BaselineVerificationResults.Single(item => item.Verifier == "Build")
            .Status.Should().Be(VerificationStatus.Pass);
        result.VerificationResults.Single(item => item.Verifier == "Build")
            .Status.Should().Be(VerificationStatus.Fail);
        result.BaselineCommands.Should().ContainSingle();
        result.CandidateCommands.Should().ContainSingle();
        agent.WasCalled.Should().BeTrue();
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task BaselineBuildFailure_IsRejectedBeforeAgentRuns_AndPersisted()
    {
        var project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Target Name="FailBuild" BeforeTargets="CoreCompile">
                <Error Text="intentional baseline build failure" />
              </Target>
            </Project>
            """;
        await using var repository = await TemporaryGitRepository.CreateAsync(
            new Dictionary<string, string> { ["Fixture.csproj"] = project });
        var agent = Agent.Success(Response("src/new.txt"));
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var pipeline = new StagedExecutionPipeline(agent, repository.ProcessRunner, store);

        var result = await pipeline.RunAsync(
            repository.Path,
            Contract(
                build: true,
                execution: new RepositoryExecutionProfile { Target = "Fixture.csproj" }),
            CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.Decision.Reason.Should().StartWith("Baseline preflight failed:");
        result.BaselineVerificationResults.Single(item => item.Verifier == "Build")
            .Status.Should().Be(VerificationStatus.Fail);
        result.VerificationResults.Should().BeEmpty();
        result.AgentResult.ExitReason.Should().Be("BaselinePreflightFailed");
        result.CandidateChangeSet.HasChanges.Should().BeFalse();
        agent.WasCalled.Should().BeFalse();

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence.Should().NotBeNull();
        evidence!.BaselineVerificationResults.Should().ContainSingle();
        evidence.BaselineCommands.Should().ContainSingle();
        evidence.AgentResult.ExitReason.Should().Be("BaselinePreflightFailed");
        evidence.StateTransitions.Should().Contain(item =>
            item.Contains("BaselineVerifying->Rejected"));
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task NestedExecutionProfile_BuildsDeclaredProject()
    {
        var project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
            </Project>
            """;
        await using var repository = await TemporaryGitRepository.CreateAsync(
            new Dictionary<string, string>
            {
                ["Backend/Fixture.csproj"] = project
            });
        var contract = Contract(
            build: true,
            execution: new RepositoryExecutionProfile
            {
                WorkingDirectory = "Backend",
                Target = "Fixture.csproj"
            });

        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var result = await new StagedExecutionPipeline(
                Agent.Success(Response("src/new.txt")),
                repository.ProcessRunner,
                store)
            .RunAsync(repository.Path, contract, CancellationToken.None);

        result.BaselineVerificationResults.Single(item => item.Verifier == "Build")
            .Status.Should().Be(VerificationStatus.Pass);
        result.VerificationResults.Single(item => item.Verifier == "Build")
            .Status.Should().Be(VerificationStatus.Pass);
        result.BaselineCommands.Should().ContainSingle(command =>
            command.WorkingDirectory == "Backend" &&
            command.Arguments.SequenceEqual(new[] { "build", "Fixture.csproj" }));
        result.CandidateCommands.Should().ContainSingle(command =>
            command.WorkingDirectory == "Backend" &&
            command.Arguments.SequenceEqual(new[] { "build", "Fixture.csproj" }));
        result.Decision.Decision.Should().Be(TaskDecision.Verified);

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence.Should().NotBeNull();
        evidence!.BaselineCommands.Should().ContainSingle();
        evidence.CandidateCommands.Should().ContainSingle();
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task InvalidExecutionProfile_IsRejectedBeforeAgentRuns()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var agent = Agent.Success(Response("src/new.txt"));
        var contract = Contract(
            build: true,
            execution: new RepositoryExecutionProfile
            {
                WorkingDirectory = "../outside",
                Target = "Fixture.csproj"
            });

        var result = await Pipeline(repository, agent)
            .RunAsync(repository.Path, contract, CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.BaselineVerificationResults.Single(item => item.Verifier == "Build")
            .Status.Should().Be(VerificationStatus.Error);
        result.BaselineCommands.Should().BeEmpty();
        agent.WasCalled.Should().BeFalse();
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TestFailure_RejectsCandidate()
    {
        var project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
              </PropertyGroup>
              <Target Name="FailTests" BeforeTargets="VSTest" Condition="Exists('src/fail-tests.flag')">
                <Error Text="intentional test failure" />
              </Target>
            </Project>
            """;
        await using var repository = await TemporaryGitRepository.CreateAsync(
            new Dictionary<string, string> { ["Fixture.csproj"] = project });
        var contract = Contract(
            build: true,
            tests: true,
            execution: new RepositoryExecutionProfile { Target = "Fixture.csproj" });

        var agent = Agent.Success(Response("src/fail-tests.flag"));
        var result = await Pipeline(repository, agent)
            .RunAsync(repository.Path, contract, CancellationToken.None);

        result.BaselineVerificationResults.Should().OnlyContain(item =>
            item.Status == VerificationStatus.Pass);
        result.VerificationResults.Single(item => item.Verifier == "Build")
            .Status.Should().Be(VerificationStatus.Pass);
        result.VerificationResults.Single(item => item.Verifier == "Tests")
            .Status.Should().Be(VerificationStatus.Fail);
        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.BaselineCommands.Should().HaveCount(2);
        result.CandidateCommands.Should().HaveCount(2);
        agent.WasCalled.Should().BeTrue();
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DirtyRepository_FailsClosedBeforeAgentRuns()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        await File.WriteAllTextAsync(System.IO.Path.Combine(repository.Path, "dirty.txt"), "dirty");
        var agent = Agent.Success(Response("src/new.txt"));

        var action = () => Pipeline(repository, agent)
            .RunAsync(repository.Path, Contract(), CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*dirty repository*");
        agent.WasCalled.Should().BeFalse();
    }

    [Fact]
    public async Task EvidencePathInsideTargetRepository_IsRejectedBeforeAgentRuns()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var agent = Agent.Success(Response("src/new.txt"));
        var store = new JsonExecutionEvidenceStore(
            System.IO.Path.Combine(repository.Path, ".aecs", "evidence"));
        var pipeline = new StagedExecutionPipeline(agent, repository.ProcessRunner, store);

        var action = () => pipeline.RunAsync(
            repository.Path,
            Contract(),
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*must be outside target repository*");
        agent.WasCalled.Should().BeFalse();
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task ProcessTimeout_TerminatesProcessAndReturnsTimedOut()
    {
        var runner = new SystemProcessRunner();
        var request = OperatingSystem.IsWindows()
            ? new ProcessExecutionRequest
            {
                FileName = "powershell",
                Arguments = ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
                WorkingDirectory = System.IO.Path.GetTempPath(),
                Timeout = TimeSpan.FromMilliseconds(200)
            }
            : new ProcessExecutionRequest
            {
                FileName = "sh",
                Arguments = ["-c", "sleep 30"],
                WorkingDirectory = System.IO.Path.GetTempPath(),
                Timeout = TimeSpan.FromMilliseconds(200)
            };

        var result = await runner.RunAsync(request, CancellationToken.None);

        result.TimedOut.Should().BeTrue();
        result.Succeeded.Should().BeFalse();
        result.Duration.Should().BeLessThan(TimeSpan.FromSeconds(5));
    }

    private static StagedExecutionPipeline Pipeline(
        TemporaryGitRepository repository,
        Agent agent) => new(
        agent,
        repository.ProcessRunner,
        new JsonExecutionEvidenceStore(repository.EvidencePath));

    private static TaskContract Contract(
        bool build = false,
        bool tests = false,
        RepositoryExecutionProfile? execution = null) => new()
    {
        Id = $"E2E-{Guid.NewGuid():N}",
        Objective = "Produce a staged candidate",
        Scope = new ScopeDefinition { Allowed = ["src/**"] },
        Budget = new ExecutionBudget
        {
            MaxTokens = 10000,
            MaxCostUsd = 10m,
            MaxRetries = 0,
            MaxDurationSeconds = 60,
            MaxFilesChanged = 10
        },
        Execution = execution ?? new RepositoryExecutionProfile(),
        Verification = new VerificationProfile
        {
            Build = build,
            UnitTests = tests,
            Scope = true,
            Budget = true
        },
        Approval = new ApprovalPolicy { Production = ApprovalLevel.None }
    };

    private static string Response(string path) =>
        $"FILE: {path}\n```text\ncandidate\n```";

    private sealed class Agent : IAgentAdapter
    {
        private readonly AgentRunResult _result;
        private readonly Action<string>? _workspaceAction;

        private Agent(AgentRunResult result, Action<string>? workspaceAction = null)
        {
            _result = result;
            _workspaceAction = workspaceAction;
        }

        public bool WasCalled { get; private set; }
        public AgentExecutionRequest? LastRequest { get; private set; }

        public Task<AgentRunResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            WasCalled = true;
            LastRequest = request;
            _workspaceAction?.Invoke(request.RepoPath);
            return Task.FromResult(_result);
        }

        public static Agent Success(
            string output,
            List<string>? claimedFiles = null,
            Action<string>? workspaceAction = null) => new(new AgentRunResult
        {
            Success = true,
            StdOut = output,
            ExitCode = 0,
            Duration = TimeSpan.FromMilliseconds(10),
            InputTokens = 10,
            OutputTokens = 10,
            EstimatedCost = 0.01m,
            FilesChanged = claimedFiles ?? [],
            ExitReason = "Completed"
        }, workspaceAction);

        public static Agent Success(string output, Action<string> workspaceAction) =>
            Success(output, null, workspaceAction);

        public static Agent Failure(string output) => new(new AgentRunResult
        {
            Success = false,
            StdOut = output,
            StdErr = "agent failed",
            ExitCode = 1,
            Duration = TimeSpan.FromMilliseconds(10),
            ExitReason = "Failed"
        });
    }

    private sealed class TemporaryGitRepository : IAsyncDisposable
    {
        private TemporaryGitRepository(string rootPath)
        {
            RootPath = rootPath;
            Path = System.IO.Path.Combine(rootPath, "repository");
            EvidencePath = System.IO.Path.Combine(rootPath, "evidence");
        }

        public string RootPath { get; }
        public string Path { get; }
        public string EvidencePath { get; }
        public SystemProcessRunner ProcessRunner { get; } = new();

        public static async Task<TemporaryGitRepository> CreateAsync(
            IReadOnlyDictionary<string, string>? additionalFiles = null)
        {
            var root = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                $"aecs-staged-e2e-{Guid.NewGuid():N}");
            var repository = new TemporaryGitRepository(root);
            Directory.CreateDirectory(repository.Path);
            await File.WriteAllTextAsync(
                System.IO.Path.Combine(repository.Path, "README.md"),
                "baseline");

            if (additionalFiles is not null)
            {
                foreach (var (relativePath, content) in additionalFiles)
                {
                    var fullPath = System.IO.Path.Combine(repository.Path, relativePath);
                    Directory.CreateDirectory(System.IO.Path.GetDirectoryName(fullPath)!);
                    await File.WriteAllTextAsync(fullPath, content);
                }
            }

            await repository.GitAsync("init");
            await repository.GitAsync("config", "user.email", "aecs-tests@example.invalid");
            await repository.GitAsync("config", "user.name", "AECS Tests");
            await repository.GitAsync("add", "-A");
            await repository.GitAsync("commit", "-m", "baseline");
            return repository;
        }

        public async Task<string> StatusAsync() =>
            (await GitAsync("status", "--porcelain=v1", "--untracked-files=all"))
            .StandardOutput;

        public ValueTask DisposeAsync()
        {
            var resolved = System.IO.Path.GetFullPath(RootPath);
            var tempRoot = System.IO.Path.GetFullPath(System.IO.Path.GetTempPath());
            if (resolved.StartsWith(tempRoot, StringComparison.OrdinalIgnoreCase) && Directory.Exists(resolved))
            {
                foreach (var file in Directory.EnumerateFiles(resolved, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(resolved, recursive: true);
            }

            return ValueTask.CompletedTask;
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

            result.Succeeded.Should().BeTrue(
                $"git {string.Join(' ', arguments)} failed: {result.StandardError}");
            return result;
        }
    }
}
