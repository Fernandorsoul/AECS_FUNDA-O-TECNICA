using AECS.Application.Verification;
using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class AcceptanceCriteriaExecutionTests
{
    [Fact]
    public async Task TargetedEvidence_ExecutesRealFilteredTestAndRequiresTrxResult()
    {
        var repositoryPath = Path.GetFullPath(Path.Combine(
            AppContext.BaseDirectory,
            "..", "..", "..", "..", ".."));
        var runner = new SystemProcessRunner();
        var commit = (await GitAsync(runner, repositoryPath, "rev-parse", "HEAD"))
            .StandardOutput.Trim();
        var branch = (await GitAsync(
                runner,
                repositoryPath,
                "rev-parse",
                "--abbrev-ref",
                "HEAD"))
            .StandardOutput.Trim();
        var manager = new GitWorkspaceManager(runner);
        await using var workspace = await manager.CreateWorkspaceAsync(
            new BaselineSnapshot
            {
                Commit = commit,
                Branch = branch,
                GitStatus = string.Empty,
                RepositoryPath = repositoryPath
            },
            CancellationToken.None);

        var build = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "dotnet",
            Arguments = ["build", "AECS.sln", "--disable-build-servers", "--nologo", "--verbosity", "quiet"],
            WorkingDirectory = workspace.Path,
            Timeout = TimeSpan.FromMinutes(2)
        }, CancellationToken.None);
        build.Succeeded.Should().BeTrue(build.StandardError);

        var criterion = new AcceptanceCriterion
        {
            Id = "AC-001",
            Description = "The declared regression test passes",
            Evidence = new AcceptanceEvidenceRequirement
            {
                Type = AcceptanceEvidenceType.Test,
                Reference = "FullyQualifiedName=AECS.UnitTests.DecisionEngineTests.Decide_EmptyResults_ReturnsRejected"
            }
        };
        var context = new VerificationContext
        {
            TaskId = "REAL-ACCEPTANCE",
            AgentRunId = Guid.NewGuid().ToString("N"),
            RepoPath = workspace.Path,
            Contract = new TaskContract
            {
                Id = "REAL-ACCEPTANCE",
                AcceptanceCriteria = [criterion.Description],
                AcceptanceRequirements = [criterion],
                Budget = new ExecutionBudget
                {
                    MaxTokens = 10_000,
                    MaxCostUsd = 1m,
                    MaxRetries = 0,
                    MaxDurationSeconds = 120,
                    MaxFilesChanged = 5
                },
                Execution = new RepositoryExecutionProfile { Target = "AECS.sln" },
                Verification = new VerificationProfile { Build = true, UnitTests = true }
            },
            CandidateChangeSet = new CandidateChangeSet { Diff = "diff" },
            CommandEvidence = []
        };

        var outcome = await new AcceptanceCriteriaVerifier(runner).VerifyAsync(
            context,
            [
                new VerificationResult
                {
                    AgentRunId = context.AgentRunId,
                    Verifier = "Tests",
                    Status = VerificationStatus.Pass,
                    Severity = Severity.Info,
                    Message = "Aggregate tests passed"
                }
            ],
            canExecuteTests: true,
            CancellationToken.None);

        outcome.AggregateResult.Status.Should().Be(VerificationStatus.Pass);
        outcome.Criteria.Should().ContainSingle(result =>
            result.Status == VerificationStatus.Pass &&
            result.Message.Contains("1/1") &&
            result.EvidenceReferences.Any(reference =>
                reference.StartsWith("execution-command:")));
        context.CommandEvidence.Should().ContainSingle(command =>
            command.FileName == "dotnet" &&
            command.Arguments.Contains("--filter"));
        Directory.Exists(Path.Combine(workspace.Path, ".aecs-verification"))
            .Should().BeFalse();
    }

    private static async Task<ProcessExecutionResult> GitAsync(
        SystemProcessRunner runner,
        string repositoryPath,
        params string[] arguments)
    {
        var result = await runner.RunAsync(new ProcessExecutionRequest
        {
            FileName = "git",
            Arguments = arguments,
            WorkingDirectory = repositoryPath,
            Timeout = TimeSpan.FromSeconds(30)
        }, CancellationToken.None);
        result.Succeeded.Should().BeTrue(result.StandardError);
        return result;
    }
}
