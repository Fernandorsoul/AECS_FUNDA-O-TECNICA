using AECS.Application.Replay;
using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Exceptions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class ExecutionReplayTests
{
    [Fact]
    public async Task Replay_ReconstructsCandidateAndGatesWithoutCallingAgent()
    {
        await using var fixture = await ReplayFixture.CreateAsync();
        var execution = await fixture.ExecuteAsync();
        var before = await fixture.RepositorySnapshotAsync();
        var worktreesBefore = await fixture.WorktreesAsync();

        var replay = await fixture.ReplayAsync(execution.EvidenceId);

        replay.Outcome.Should().Be(ExecutionReplayOutcome.Reproduced);
        replay.Evidence.ActualDiffHash.Should().Be(execution.CandidateChangeSet.DiffHash);
        replay.Evidence.ExpectedRepositorySnapshotHash.Should()
            .Be(execution.RepositorySnapshot.SnapshotHash);
        replay.Evidence.ActualRepositorySnapshotHash.Should()
            .Be(execution.RepositorySnapshot.SnapshotHash);
        replay.Evidence.RepositorySnapshotDiff.Should().NotBeNull();
        replay.Evidence.RepositorySnapshotDiff!.HasChanges.Should().BeFalse();
        replay.Evidence.Tools.Should().OnlyContain(item =>
            item.Status == ReplayComparisonStatus.Match);
        replay.Evidence.Commands.Should().OnlyContain(item =>
            item.Definition == ReplayComparisonStatus.Match &&
            item.Result == ReplayComparisonStatus.Match);
        replay.Evidence.Gates.Should().OnlyContain(item =>
            item.Status == ReplayComparisonStatus.Match);
        fixture.Agent.Calls.Should().Be(1, "replay must never invoke the agent adapter");
        (await fixture.RepositorySnapshotAsync()).Should().Be(before);
        (await fixture.WorktreesAsync()).Should().Be(worktreesBefore);

        (await fixture.Store.LoadAsync(execution.EvidenceId, CancellationToken.None))
            .Should().NotBeNull("the appended replay chain must remain verifiable");
        var envelope = await File.ReadAllTextAsync(execution.EvidenceLocation);
        envelope.Should().Contain("\"replayEvents\"")
            .And.Contain($"\"id\": \"{replay.Evidence.Id}\"");
    }

    [Fact]
    public async Task Replay_ClassifiesUnavailableBaselineAndPersistsTheEvent()
    {
        await using var fixture = await ReplayFixture.CreateAsync();
        var execution = await fixture.ExecuteAsync();
        fixture.Runner.BaselineAvailable = false;

        var replay = await fixture.ReplayAsync(execution.EvidenceId);

        replay.Outcome.Should().Be(ExecutionReplayOutcome.BaselineUnavailable);
        replay.Message.Should().Contain("unavailable");
        (await File.ReadAllTextAsync(execution.EvidenceLocation))
            .Should().Contain("is unavailable in the requested repository");
    }

    [Fact]
    public async Task Replay_ClassifiesToolVersionChangeAsEnvironmentDivergence()
    {
        await using var fixture = await ReplayFixture.CreateAsync();
        var execution = await fixture.ExecuteAsync();
        fixture.Runner.DotnetVersion = "10.0.0";

        var replay = await fixture.ReplayAsync(execution.EvidenceId);

        replay.Outcome.Should().Be(ExecutionReplayOutcome.EnvironmentDivergence);
        replay.Evidence.ActualDiffHash.Should().Be(execution.CandidateChangeSet.DiffHash);
        replay.Evidence.Tools.Should().ContainSingle(item =>
            item.Tool == "dotnet" && item.Status == ReplayComparisonStatus.Diverged);
        replay.Evidence.ActualRepositorySnapshotHash.Should()
            .Be(replay.Evidence.ExpectedRepositorySnapshotHash,
                "tool provenance is authenticated separately from repository content addressing");
        replay.Evidence.RepositorySnapshotDiff!.HasChanges.Should().BeFalse(
            "the inventory files match even though the authenticated tool environment changed");
    }

    [Fact]
    public async Task Replay_RejectsTamperedAuthenticatedEvidenceBeforeCreatingAnEvent()
    {
        await using var fixture = await ReplayFixture.CreateAsync();
        var execution = await fixture.ExecuteAsync();
        var json = await File.ReadAllTextAsync(execution.EvidenceLocation);
        var tamperedHash = execution.CandidateChangeSet.DiffHash[..^1] +
            (execution.CandidateChangeSet.DiffHash[^1] == '0' ? '1' : '0');
        await File.WriteAllTextAsync(
            execution.EvidenceLocation,
            json.Replace(
                execution.CandidateChangeSet.DiffHash,
                tamperedHash,
                StringComparison.Ordinal));

        var replay = () => fixture.ReplayAsync(execution.EvidenceId);

        await replay.Should().ThrowAsync<EvidenceIntegrityException>();
        (await File.ReadAllTextAsync(execution.EvidenceLocation))
            .Should().Contain("\"replayEvents\": []");
    }

    [Fact]
    public async Task Replay_ClassifiesRequiredUnsupportedGateSeparately()
    {
        await using var fixture = await ReplayFixture.CreateAsync();
        var execution = await fixture.ExecuteAsync("ExternalPolicyGate");

        var replay = await fixture.ReplayAsync(execution.EvidenceId);

        replay.Outcome.Should().Be(ExecutionReplayOutcome.GateNotReproducible);
        replay.Evidence.Gates.Should().ContainSingle(item =>
            item.Gate == "candidate:ExternalPolicyGate" &&
            item.Status == ReplayComparisonStatus.NotReproducible);
    }

    [Fact]
    public async Task Replay_ReproducesNormalizedSecurityScanEvidence()
    {
        await using var fixture = await ReplayFixture.CreateAsync();
        var execution = await fixture.ExecuteAsync(securityScan: true);

        var replay = await fixture.ReplayAsync(execution.EvidenceId);

        replay.Outcome.Should().Be(ExecutionReplayOutcome.Reproduced);
        replay.Evidence.Gates.Should().Contain(item =>
            item.Gate == "baseline:SecurityScan" &&
            item.Status == ReplayComparisonStatus.Match);
        replay.Evidence.Gates.Should().Contain(item =>
            item.Gate == "candidate:SecurityScan" &&
            item.Status == ReplayComparisonStatus.Match);
    }

    [Fact]
    public async Task Replay_ReproducesIndependentTestSuiteEvidence()
    {
        await using var fixture = await ReplayFixture.CreateAsync();
        var execution = await fixture.ExecuteAsync(testSuites: true);

        var replay = await fixture.ReplayAsync(execution.EvidenceId);

        replay.Outcome.Should().Be(ExecutionReplayOutcome.Reproduced);
        foreach (var gate in new[]
                 {
                     TestSuiteVerifier.UnitName,
                     TestSuiteVerifier.IntegrationName,
                     TestSuiteVerifier.AcceptanceName
                 })
        {
            replay.Evidence.Gates.Should().Contain(item =>
                item.Gate == $"baseline:{gate}" &&
                item.Status == ReplayComparisonStatus.Match);
            replay.Evidence.Gates.Should().Contain(item =>
                item.Gate == $"candidate:{gate}" &&
                item.Status == ReplayComparisonStatus.Match);
        }
    }

    private sealed class ReplayFixture : IAsyncDisposable
    {
        private const string RootPrefix = "aecs-replay-tests-";

        private ReplayFixture(string rootPath)
        {
            RootPath = rootPath;
            RepositoryPath = Path.Combine(rootPath, "repository");
            Store = new JsonExecutionEvidenceStore(Path.Combine(rootPath, "evidence"));
        }

        public string RootPath { get; }
        public string RepositoryPath { get; }
        public ReplayProcessRunner Runner { get; } = new();
        public CountingAgent Agent { get; } = new();
        public DeterministicSecurityScanner SecurityScanner { get; } = new();
        public JsonExecutionEvidenceStore Store { get; }

        public static async Task<ReplayFixture> CreateAsync()
        {
            var rootPath = Path.Combine(Path.GetTempPath(), $"{RootPrefix}{Guid.NewGuid():N}");
            var fixture = new ReplayFixture(rootPath);
            Directory.CreateDirectory(fixture.RepositoryPath);
            await File.WriteAllTextAsync(
                Path.Combine(fixture.RepositoryPath, "ReplayFixture.sln"),
                "Microsoft Visual Studio Solution File, Format Version 12.00\n");
            await fixture.RequiredGitAsync("init", "--initial-branch=main");
            await fixture.RequiredGitAsync("config", "user.email", "aecs-replay@example.invalid");
            await fixture.RequiredGitAsync("config", "user.name", "AECS Replay Tests");
            await fixture.RequiredGitAsync("config", "core.autocrlf", "false");
            await fixture.RequiredGitAsync("add", "-A");
            await fixture.RequiredGitAsync("commit", "-m", "baseline");
            return fixture;
        }

        public Task<StagedExecutionResult> ExecuteAsync(
            string? requiredGate = null,
            bool securityScan = false,
            bool testSuites = false)
        {
            var required = requiredGate is null ? new List<string>() : [requiredGate];
            var contract = new TaskContract
            {
                Id = $"TASK-REPLAY-{Guid.NewGuid():N}",
                Objective = "Create a deterministic replay fixture",
                Scope = new ScopeDefinition { Allowed = ["feature.txt"] },
                Execution = new RepositoryExecutionProfile
                {
                    Target = "ReplayFixture.sln",
                    Runtime = RepositoryExecutionProfile.HostRuntime,
                    TestSuites = testSuites
                        ? new TestSuiteMatrix
                        {
                            Unit = Suite(TestGateMode.Required),
                            Integration = Suite(TestGateMode.Optional),
                            Acceptance = Suite(TestGateMode.Required)
                        }
                        : null
                },
                Verification = new VerificationProfile
                {
                    Build = true,
                    UnitTests = true,
                    SecurityScan = securityScan,
                    SecurityPolicy = securityScan
                        ? new SecurityScanPolicy { Scanners = [SecurityScannerIds.Secrets] }
                        : null,
                    RequiredSemanticVerifiers = required
                },
                AcceptanceRequirements =
                [
                    new AcceptanceCriterion
                    {
                        Id = "AC-001",
                        Description = "Candidate has a real change",
                        Evidence = new AcceptanceEvidenceRequirement
                        {
                            Type = AcceptanceEvidenceType.Test,
                            Reference = "FullyQualifiedName~ReplayFixture"
                        }
                    }
                ]
            };
            return new StagedExecutionPipeline(
                Agent,
                Runner,
                Store,
                securityScanners: securityScan ? [SecurityScanner] : null).RunAsync(
                RepositoryPath,
                contract,
                CancellationToken.None);
        }

        private static TestSuiteCommandProfile Suite(TestGateMode mode) => new()
        {
            Mode = mode,
            Target = "ReplayFixture.sln",
            TimeoutSeconds = 30
        };

        public Task<ExecutionReplayResult> ReplayAsync(Guid evidenceId) =>
            new ExecutionReplayService(
                Runner,
                Store,
                securityScanners: [SecurityScanner]).ReplayAsync(
                new ExecutionReplayRequest
                {
                    EvidenceId = evidenceId,
                    RepositoryPath = RepositoryPath
                },
                CancellationToken.None);

        public async Task<string> RepositorySnapshotAsync()
        {
            var head = await RequiredGitAsync("rev-parse", "HEAD");
            var branch = await RequiredGitAsync("rev-parse", "--abbrev-ref", "HEAD");
            var status = await RequiredGitAsync(
                "status", "--porcelain=v1", "--untracked-files=all");
            return $"{head.StandardOutput.Trim()}|{branch.StandardOutput.Trim()}|{status.StandardOutput}";
        }

        public async Task<string> WorktreesAsync() =>
            (await RequiredGitAsync("worktree", "list", "--porcelain")).StandardOutput;

        private async Task<ProcessExecutionResult> RequiredGitAsync(params string[] arguments)
        {
            var result = await Runner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = RepositoryPath,
                Timeout = TimeSpan.FromSeconds(30)
            }, CancellationToken.None);
            if (!result.Succeeded)
                throw new InvalidOperationException(result.StandardError);
            return result;
        }

        public ValueTask DisposeAsync()
        {
            var root = Path.GetFullPath(RootPath);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!root.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith(RootPrefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException($"Refusing to delete unexpected path: {root}");
            }

            if (Directory.Exists(root))
            {
                foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }
    }

    private sealed class CountingAgent : IAgentAdapter
    {
        public int Calls { get; private set; }

        public Task<AgentRunResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            return Task.FromResult(new AgentRunResult
            {
                Success = true,
                ExitCode = 0,
                ExitReason = "Completed",
                StdOut = """
                    FILE: feature.txt
                    ```text
                    replayed candidate
                    ```
                    """,
                FilesChanged = ["feature.txt"]
            });
        }
    }

    private sealed class DeterministicSecurityScanner : ISecurityScanner
    {
        public string Id => SecurityScannerIds.Secrets;

        public Task<SecurityScannerResult> ScanAsync(
            SecurityScannerContext context,
            CancellationToken cancellationToken) => Task.FromResult(new SecurityScannerResult
            {
                Scanner = Id,
                Category = "secret",
                Version = "replay-fixture/1",
                ConfigurationVersion = "replay-fixture/1",
                Conclusive = true,
                Message = "Deterministic replay scanner completed."
            });
    }

    private sealed class ReplayProcessRunner : IProcessRunner
    {
        private readonly SystemProcessRunner _system = new();

        public string DotnetVersion { get; set; } = "9.0.100";
        public bool BaselineAvailable { get; set; } = true;

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken)
        {
            if (string.Equals(request.FileName, "git", StringComparison.OrdinalIgnoreCase) &&
                request.Arguments.Count >= 2 &&
                request.Arguments[0] == "cat-file" &&
                !BaselineAvailable)
            {
                return Task.FromResult(new ProcessExecutionResult
                {
                    ExitCode = 1,
                    StandardError = "unknown revision"
                });
            }

            if (!string.Equals(request.FileName, "dotnet", StringComparison.OrdinalIgnoreCase))
                return _system.RunAsync(request, cancellationToken);

            return SimulateDotnetAsync(request, cancellationToken);
        }

        private async Task<ProcessExecutionResult> SimulateDotnetAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken)
        {
            var arguments = request.Arguments.ToList();
            var resultsIndex = arguments.IndexOf("--results-directory");
            if (resultsIndex >= 0 && resultsIndex + 1 < arguments.Count)
            {
                var resultsDirectory = arguments[resultsIndex + 1];
                Directory.CreateDirectory(resultsDirectory);
                await File.WriteAllTextAsync(
                    Path.Combine(resultsDirectory, "replay.trx"),
                    "<TestRun><Results><UnitTestResult outcome=\"Passed\" /></Results></TestRun>",
                    cancellationToken);
            }

            return new ProcessExecutionResult
            {
                ExitCode = 0,
                StandardOutput = request.Arguments.SequenceEqual(["--version"])
                    ? DotnetVersion + Environment.NewLine
                    : "simulated deterministic command succeeded" + Environment.NewLine,
                Duration = TimeSpan.FromMilliseconds(10)
            };
        }
    }
}
