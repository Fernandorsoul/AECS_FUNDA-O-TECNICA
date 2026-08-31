using AECS.Application.Staging;
using AECS.Application.Verification;
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
        result.RepositorySnapshot.SnapshotHash.Should().StartWith("sha256:");
        result.RepositorySnapshot.BaselineCommit.Should().Be(result.Baseline.Commit);
        result.RepositorySnapshot.TaskContractId.Should().Be(result.Contract.Id);
        result.RepositorySnapshot.Files.Should().ContainSingle(file => file.Path == "README.md");
        result.CSharpSymbolGraph.GraphHash.Should().StartWith("sha256:");
        result.CSharpSymbolGraph.RepositorySnapshotHash.Should()
            .Be(result.RepositorySnapshot.SnapshotHash);
        result.OriginalRepositoryUnchanged.Should().BeTrue();
        File.Exists(System.IO.Path.Combine(repository.Path, "src", "new-file.txt")).Should().BeFalse();
        (await repository.StatusAsync()).Should().BeEmpty();

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence.Should().NotBeNull();
        evidence!.CandidateChangeSet.DiffHash.Should().Be(result.CandidateChangeSet.DiffHash);
        evidence.RepositorySnapshot.Should().NotBeNull();
        evidence.RepositorySnapshot!.SnapshotHash.Should().Be(result.RepositorySnapshot.SnapshotHash);
        evidence.CSharpSymbolGraph.Should().NotBeNull();
        evidence.CSharpSymbolGraph!.GraphHash.Should().Be(result.CSharpSymbolGraph.GraphHash);
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
        agent.LastRequest.ContextPrompt.Should().Contain("class ExistingHandler");
        agent.LastRequest.ContextPrompt.Should().NotContain("Symbols:");
        result.ContextManifest.Source.Should().Be("isolated-git-worktree");
        result.ContextManifest.SemanticIndex.Should().Be("textual-file-inventory");
        result.ContextManifest.SymbolGraphHash.Should().BeNull();
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
    public async Task AcceptanceCriterionWithPassingVerifierEvidence_IsVerifiedAndPersisted()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var criterion = new AcceptanceCriterion
        {
            Id = "AC-001",
            Description = "A real candidate change is produced",
            Evidence = new AcceptanceEvidenceRequirement
            {
                Type = AcceptanceEvidenceType.Verifier,
                Reference = "NonEmptyChange"
            }
        };

        var result = await new StagedExecutionPipeline(
                Agent.Success(Response("src/new-file.txt")),
                repository.ProcessRunner,
                store)
            .RunAsync(
                repository.Path,
                ContractWithAcceptance(criterion),
                CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Verified);
        result.VerificationResults.Single(item => item.Verifier == "AcceptanceCriteria")
            .Status.Should().Be(VerificationStatus.Pass);
        result.AcceptanceCriteriaResults.Should().ContainSingle(item =>
            item.CriterionId == "AC-001" &&
            item.Status == VerificationStatus.Pass &&
            item.EvidenceReferences.Count == 1);
        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence!.AcceptanceCriteriaResults.Should().BeEquivalentTo(
            result.AcceptanceCriteriaResults);
        var json = await File.ReadAllTextAsync(result.EvidenceLocation);
        json.Should().Contain("\"acceptanceCriteriaResults\"");
        json.Should().Contain("\"criterionId\": \"AC-001\"");
        json.Should().Contain("\"evidenceReferences\"");
    }

    [Fact]
    public async Task TextualAcceptanceWithoutExecutableEvidence_IsRejectedFailClosed()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();

        var result = await Pipeline(repository, Agent.Success(Response("src/new-file.txt")))
            .RunAsync(
                repository.Path,
                ContractWithAcceptance(criterion: null),
                CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Rejected);
        result.VerificationResults.Single(item => item.Verifier == "AcceptanceCriteria")
            .Status.Should().Be(VerificationStatus.Fail);
        result.AcceptanceCriteriaResults.Should().ContainSingle(item =>
            item.Status == VerificationStatus.Fail &&
            item.Message.Contains("No executable evidence"));
        (await repository.StatusAsync()).Should().BeEmpty();
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
    public async Task RequiredSecurityScannerWithoutValidToolProfile_FailsClosedBeforeAgent()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var contract = Contract();
        contract = new TaskContract
        {
            Id = contract.Id,
            Objective = contract.Objective,
            Scope = contract.Scope,
            Budget = contract.Budget,
            Execution = contract.Execution,
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
            failure.Contains("SecurityScan") && failure.Contains("Error"));
        result.BaselineVerificationResults.Single(item => item.Verifier == "SecurityScan")
            .Message.Should().Contain("inconclusive");
        result.AgentAttempts.Should().BeEmpty();
    }

    [Fact]
    public async Task SecurityScan_BlocksOnlyNewCandidateFindingsAndPersistsNormalizedEvidence()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var contract = Contract();
        contract = new TaskContract
        {
            Id = contract.Id,
            Objective = contract.Objective,
            Scope = contract.Scope,
            Budget = contract.Budget,
            Execution = contract.Execution,
            Verification = new VerificationProfile
            {
                Build = false,
                UnitTests = false,
                SecurityScan = true,
                SecurityPolicy = new SecurityScanPolicy
                {
                    Scanners = [SecurityScannerIds.Secrets]
                }
            },
            Approval = contract.Approval
        };
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var scanner = new BaselineAndCandidateScanner();
        var pipeline = new StagedExecutionPipeline(
            Agent.Success(Response("src/new.txt")),
            repository.ProcessRunner,
            store,
            securityScanners: [scanner]);

        var result = await pipeline.RunAsync(
            repository.Path,
            contract,
            CancellationToken.None);

        result.BaselineVerificationResults.Single(item => item.Verifier == "SecurityScan")
            .Status.Should().Be(VerificationStatus.Pass);
        var security = result.VerificationResults.Single(item =>
            item.Verifier == "SecurityScan");
        security.Status.Should().Be(VerificationStatus.Fail);
        security.SecurityScan!.Findings.Should().Contain(finding =>
            finding.Path == "src/legacy.txt" &&
            finding.Disposition == SecurityFindingDisposition.Baseline);
        security.SecurityScan.Findings.Should().Contain(finding =>
            finding.Path == "src/new.txt" &&
            finding.Disposition == SecurityFindingDisposition.New);
        result.Decision.Decision.Should().Be(TaskDecision.Rejected);

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence!.VerificationResults.Single(item => item.Verifier == "SecurityScan")
            .SecurityScan!.PolicyHash.Should().StartWith("sha256:");
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task VersionedTestMatrix_RunsDistinctSuitesInBaselineAndCandidate()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync(
            new Dictionary<string, string>
            {
                ["tests/Unit/Product.UnitTests.csproj"] =
                    "<Project Sdk=\"Microsoft.NET.Sdk\" />",
                ["tests/Integration/Product.IntegrationTests.csproj"] =
                    "<Project Sdk=\"Microsoft.NET.Sdk\" />"
            });
        var contract = Contract();
        contract = new TaskContract
        {
            Id = contract.Id,
            Objective = contract.Objective,
            Scope = contract.Scope,
            Budget = contract.Budget,
            Execution = new RepositoryExecutionProfile
            {
                Runtime = RepositoryExecutionProfile.HostRuntime,
                TestSuites = new TestSuiteMatrix
                {
                    Unit = new TestSuiteCommandProfile
                    {
                        Mode = TestGateMode.Required,
                        Target = "tests/Unit/Product.UnitTests.csproj",
                        TimeoutSeconds = 20
                    },
                    Integration = new TestSuiteCommandProfile
                    {
                        Mode = TestGateMode.Optional,
                        Target = "tests/Integration/Product.IntegrationTests.csproj",
                        TimeoutSeconds = 20
                    }
                }
            },
            Verification = new VerificationProfile
            {
                Build = false,
                UnitTests = true,
                IntegrationTests = false
            },
            Approval = contract.Approval
        };
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var staged = new MatrixTestRunner();

        var result = await new StagedExecutionPipeline(
                Agent.Success(Response("src/new-file.txt")),
                repository.ProcessRunner,
                store,
                stagedProcessRunnerFactory: new FixedStagedRunnerFactory(staged))
            .RunAsync(repository.Path, contract, CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Verified,
            "the optional integration failure must remain visible without blocking");
        result.BaselineVerificationResults.Should().Contain(result =>
            result.Verifier == TestSuiteVerifier.UnitName &&
            result.Status == VerificationStatus.Pass &&
            result.TestSuite!.Executed == 2);
        result.BaselineVerificationResults.Should().Contain(result =>
            result.Verifier == TestSuiteVerifier.IntegrationName &&
            result.Status == VerificationStatus.Fail);
        result.VerificationResults.Should().Contain(result =>
            result.Verifier == TestSuiteVerifier.UnitName &&
            result.Status == VerificationStatus.Pass);
        result.VerificationResults.Should().Contain(result =>
            result.Verifier == TestSuiteVerifier.IntegrationName &&
            result.Status == VerificationStatus.Fail);
        var suiteRequests = staged.Requests.Where(request =>
            request.Arguments.FirstOrDefault() == "test").ToList();
        suiteRequests.Should().HaveCount(4);
        suiteRequests.Select(request => request.Phase).Should().BeEquivalentTo(
        [
            ExecutionCapabilityPhases.BaselineUnitTest,
            ExecutionCapabilityPhases.BaselineIntegrationTest,
            ExecutionCapabilityPhases.CandidateUnitTest,
            ExecutionCapabilityPhases.CandidateIntegrationTest
        ]);
        suiteRequests.Should().Contain(request =>
            request.Arguments.Contains("tests/Unit/Product.UnitTests.csproj"));
        suiteRequests.Should().Contain(request =>
            request.Arguments.Contains("tests/Integration/Product.IntegrationTests.csproj"));

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence!.FinalDecision.RequiredVerifiers.Should().Contain(TestSuiteVerifier.UnitName);
        evidence.FinalDecision.RequiredVerifiers.Should().NotContain(
            TestSuiteVerifier.IntegrationName);
        var commandIds = evidence.BaselineCommands
            .Concat(evidence.CandidateCommands)
            .Select(command => command.Id)
            .ToHashSet();
        evidence.BaselineVerificationResults
            .Concat(evidence.VerificationResults)
            .Where(verification => verification.TestSuite is not null)
            .Should().HaveCount(4)
            .And.OnlyContain(verification =>
                commandIds.Contains(verification.TestSuite!.CommandEvidenceId));
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CandidateBuildFailure_IsRejectedAfterGreenBaseline()
    {
        var project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
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
        result.BaselineCommands.Should().ContainSingle(command =>
            command.Arguments.FirstOrDefault() == "build");
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
                <TargetFramework>net8.0</TargetFramework>
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
        evidence.BaselineCommands.Should().ContainSingle(command =>
            command.Arguments.FirstOrDefault() == "build");
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
                <TargetFramework>net8.0</TargetFramework>
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
        evidence!.BaselineCommands.Should().ContainSingle(command =>
            command.Arguments.FirstOrDefault() == "build");
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
        result.BaselineCommands.Should().HaveCount(2)
            .And.OnlyContain(command => command.Arguments.SequenceEqual(new[] { "--version" }));
        agent.WasCalled.Should().BeFalse();
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task TestFailure_RejectsCandidate()
    {
        var project = """
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <TargetFramework>net8.0</TargetFramework>
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
        result.BaselineCommands.Should().HaveCount(4);
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
    public async Task TransientAgentFailure_RetriesWithinBudget_AndPersistsEveryAttempt()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var agent = new SequenceAgent(
            new AgentRunResult
            {
                Success = false,
                ExitCode = 429,
                ExitReason = "RateLimited",
                FailureKind = AgentFailureKind.RateLimited,
                RetryAfter = TimeSpan.FromSeconds(1),
                InputTokens = 5,
                OutputTokens = 5,
                EstimatedCost = 0.001m,
                Duration = TimeSpan.FromMilliseconds(5)
            },
            new AgentRunResult
            {
                Success = true,
                StdOut = Response("src/retried.txt"),
                ExitCode = 0,
                ExitReason = "Completed",
                InputTokens = 10,
                OutputTokens = 10,
                EstimatedCost = 0.002m,
                Duration = TimeSpan.FromMilliseconds(5)
            });
        var pipeline = new StagedExecutionPipeline(
            agent,
            repository.ProcessRunner,
            store,
            retryDelay: (_, _) => Task.CompletedTask);

        var result = await pipeline.RunAsync(
            repository.Path,
            Contract(budget: new ExecutionBudget
            {
                MaxTokens = 100,
                MaxCostUsd = 1m,
                MaxRetries = 1,
                MaxDurationSeconds = 60,
                MaxFilesChanged = 10
            }),
            CancellationToken.None);

        result.Decision.Decision.Should().Be(TaskDecision.Verified);
        result.AgentAttempts.Should().HaveCount(2);
        result.AgentAttempts[0].WillRetry.Should().BeTrue();
        result.AgentAttempts[0].RetryDelay.Should().Be(TimeSpan.FromSeconds(1));
        result.AgentAttempts[1].Success.Should().BeTrue();
        result.AgentRun.RetryCount.Should().Be(1);
        result.AgentResult.InputTokens.Should().Be(15);
        result.AgentResult.OutputTokens.Should().Be(15);
        result.BudgetUsage.AttemptsUsed.Should().Be(2);
        result.BudgetUsage.MaximumAttempts.Should().Be(2);
        agent.Requests[1].Budget.MaxTokens.Should().Be(90);

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence.Should().NotBeNull();
        evidence!.AgentAttempts.Should().HaveCount(2);
        evidence.BudgetUsage.AttemptsUsed.Should().Be(2);
        evidence.AgentRun.RetryCount.Should().Be(1);
        (await repository.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task CallerCancellation_DuringAgent_StopsRetriesAndPersistsCancellation()
    {
        await using var repository = await TemporaryGitRepository.CreateAsync();
        using var cancellation = new CancellationTokenSource();
        var agent = new CancellingAgent(cancellation);
        var store = new JsonExecutionEvidenceStore(repository.EvidencePath);
        var contract = Contract(budget: new ExecutionBudget
        {
            MaxTokens = 100,
            MaxCostUsd = 1m,
            MaxRetries = 3,
            MaxDurationSeconds = 60,
            MaxFilesChanged = 10
        });

        var result = await new StagedExecutionPipeline(
                agent,
                repository.ProcessRunner,
                store,
                retryDelay: (_, _) => Task.CompletedTask)
            .RunAsync(repository.Path, contract, cancellation.Token);

        agent.CallCount.Should().Be(1);
        result.FinalState.Should().Be(TaskState.Cancelled);
        result.AgentAttempts.Should().ContainSingle(attempt =>
            attempt.FailureKind == AgentFailureKind.Cancelled && !attempt.WillRetry);
        result.Decision.Decision.Should().Be(TaskDecision.Rejected);

        var evidence = await store.LoadAsync(result.EvidenceId, CancellationToken.None);
        evidence.Should().NotBeNull();
        evidence!.FinalDecision.State.Should().Be(TaskState.Cancelled);
        evidence.AgentAttempts.Should().ContainSingle();
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

    [Fact]
    public async Task ProcessCancellation_TerminatesProcessTreeAndReturnsCancelled()
    {
        var runner = new SystemProcessRunner();
        var request = OperatingSystem.IsWindows()
            ? new ProcessExecutionRequest
            {
                FileName = "powershell",
                Arguments = ["-NoProfile", "-Command", "Start-Sleep -Seconds 30"],
                WorkingDirectory = System.IO.Path.GetTempPath(),
                Timeout = TimeSpan.FromSeconds(30)
            }
            : new ProcessExecutionRequest
            {
                FileName = "sh",
                Arguments = ["-c", "sleep 30"],
                WorkingDirectory = System.IO.Path.GetTempPath(),
                Timeout = TimeSpan.FromSeconds(30)
            };
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(200));

        var result = await runner.RunAsync(request, cancellation.Token);

        result.Cancelled.Should().BeTrue();
        result.TimedOut.Should().BeFalse();
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
        RepositoryExecutionProfile? execution = null,
        ExecutionBudget? budget = null) => new()
        {
            Id = $"E2E-{Guid.NewGuid():N}",
            Objective = "Produce a staged candidate",
            Scope = new ScopeDefinition { Allowed = ["src/**"] },
            Budget = budget ?? new ExecutionBudget
            {
                MaxTokens = 10000,
                MaxCostUsd = 10m,
                MaxRetries = 0,
                MaxDurationSeconds = 60,
                MaxFilesChanged = 10
            },
            Execution = new RepositoryExecutionProfile
            {
                WorkingDirectory = execution?.WorkingDirectory ?? ".",
                Target = execution?.Target ?? string.Empty,
                Runtime = execution?.Runtime ?? RepositoryExecutionProfile.HostRuntime,
                Sandbox = execution?.Sandbox
            },
            Verification = new VerificationProfile
            {
                Build = build,
                UnitTests = tests,
                Scope = true,
                Budget = true
            },
            Approval = new ApprovalPolicy { Production = ApprovalLevel.None }
        };

    private static TaskContract ContractWithAcceptance(AcceptanceCriterion? criterion)
    {
        var contract = Contract();
        return new TaskContract
        {
            Id = contract.Id,
            Objective = contract.Objective,
            AcceptanceCriteria = ["A real candidate change is produced"],
            AcceptanceRequirements = criterion is null ? [] : [criterion],
            Scope = contract.Scope,
            Budget = contract.Budget,
            Execution = contract.Execution,
            Verification = contract.Verification,
            Approval = contract.Approval
        };
    }

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

    private sealed class SequenceAgent : IAgentAdapter
    {
        private readonly Queue<AgentRunResult> _results;

        public SequenceAgent(params AgentRunResult[] results)
        {
            _results = new Queue<AgentRunResult>(results);
        }

        public List<AgentExecutionRequest> Requests { get; } = [];

        public Task<AgentRunResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class CancellingAgent : IAgentAdapter
    {
        private readonly CancellationTokenSource _cancellation;

        public CancellingAgent(CancellationTokenSource cancellation)
        {
            _cancellation = cancellation;
        }

        public int CallCount { get; private set; }

        public Task<AgentRunResult> ExecuteAsync(
            AgentExecutionRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            _cancellation.Cancel();
            return Task.FromResult(new AgentRunResult
            {
                Success = false,
                ExitCode = -1,
                ExitReason = "Cancelled",
                FailureKind = AgentFailureKind.Cancelled,
                Duration = TimeSpan.FromMilliseconds(1)
            });
        }
    }

    private sealed class BaselineAndCandidateScanner : ISecurityScanner
    {
        public string Id => SecurityScannerIds.Secrets;

        public Task<SecurityScannerResult> ScanAsync(
            SecurityScannerContext context,
            CancellationToken cancellationToken)
        {
            var findings = new List<SecurityFinding>
            {
                Finding("src/legacy.txt", "legacy")
            };
            if (!context.IsBaseline)
                findings.Add(Finding("src/new.txt", "candidate"));
            return Task.FromResult(new SecurityScannerResult
            {
                Scanner = Id,
                Category = "secret",
                Version = "fixture/1",
                ConfigurationVersion = "fixture/1",
                Conclusive = true,
                Message = "Fixture scan completed.",
                Findings = findings
            });
        }

        private static SecurityFinding Finding(string path, string identity) => new()
        {
            Scanner = SecurityScannerIds.Secrets,
            Category = "secret",
            Rule = "FIXTURE-SECRET",
            Severity = Severity.Error,
            Path = path,
            Fingerprint = SecurityFindingFingerprint.Create(
                "FIXTURE-SECRET",
                path,
                identity),
            Message = "Synthetic normalized finding."
        };
    }

    private sealed class FixedStagedRunnerFactory : IStagedProcessRunnerFactory
    {
        private readonly IProcessRunner _runner;

        public FixedStagedRunnerFactory(IProcessRunner runner)
        {
            _runner = runner;
        }

        public Task<IProcessRunner> CreateAsync(
            string stagedWorkspacePath,
            RepositoryExecutionProfile profile,
            CancellationToken cancellationToken) => Task.FromResult(_runner);
    }

    private sealed class MatrixTestRunner : IProcessRunner
    {
        public List<ProcessExecutionRequest> Requests { get; } = [];

        public Task<ProcessExecutionResult> RunAsync(
            ProcessExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Arguments.FirstOrDefault() != "test")
            {
                return Task.FromResult(new ProcessExecutionResult
                {
                    ExitCode = 0,
                    StandardOutput = request.FileName == "dotnet" ? "9.0.100" : "git version 2.50",
                    Environment = Evidence(request)
                });
            }

            var arguments = request.Arguments.ToList();
            var resultsDirectory = arguments[arguments.IndexOf("--results-directory") + 1];
            Directory.CreateDirectory(resultsDirectory);
            var integration = arguments.Contains(
                "tests/Integration/Product.IntegrationTests.csproj");
            File.WriteAllText(
                Path.Combine(resultsDirectory, "suite.trx"),
                integration
                    ? "<TestRun><ResultSummary><Counters total=\"1\" executed=\"1\" passed=\"0\" failed=\"1\" notExecuted=\"0\" /></ResultSummary></TestRun>"
                    : "<TestRun><ResultSummary><Counters total=\"2\" executed=\"2\" passed=\"2\" failed=\"0\" notExecuted=\"0\" /></ResultSummary></TestRun>");
            return Task.FromResult(new ProcessExecutionResult
            {
                ExitCode = integration ? 1 : 0,
                Duration = TimeSpan.FromMilliseconds(10),
                Environment = Evidence(request)
            });
        }

        private static ExecutionEnvironmentEvidence Evidence(ProcessExecutionRequest request)
        {
            var capabilities = ExecutionCapabilityPolicy.TestSuitesDefault(new TestSuiteMatrix
            {
                Unit = new TestSuiteCommandProfile
                {
                    Mode = TestGateMode.Required,
                    Target = "tests/Unit/Product.UnitTests.csproj"
                },
                Integration = new TestSuiteCommandProfile
                {
                    Mode = TestGateMode.Optional,
                    Target = "tests/Integration/Product.IntegrationTests.csproj"
                }
            });
            return new ExecutionEnvironmentEvidence
            {
                Runtime = RepositoryExecutionProfile.HostRuntime,
                RuntimeVersion = "fixture",
                NetworkMode = "host",
                CpuLimit = "unlimited",
                MemoryLimit = "unlimited",
                WallClockLimitSeconds = (int)Math.Ceiling(request.Timeout.TotalSeconds),
                WorkspaceMount = "host-direct",
                DevelopmentHostOverride = true,
                Capabilities = new ExecutionCapabilityEvidence
                {
                    PolicyVersion = capabilities.Version,
                    Authority = capabilities.Authority,
                    PolicyHash = ExecutionCapabilityPolicyFingerprint.Create(capabilities),
                    Phase = request.Phase,
                    Granted = ["fixture"]
                }
            };
        }
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
