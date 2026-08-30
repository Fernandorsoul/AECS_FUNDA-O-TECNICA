using System.Security.Cryptography;
using System.Text;
using AECS.Application.Promotion;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class CandidatePromotionTests
{
    [Fact]
    public async Task VerifiedCandidate_WithExplicitConfirmation_IsAppliedAndAudited()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync();

        var result = await fixture.Service.PromoteAsync(
            fixture.Request(evidence),
            CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Promoted);
        result.Evidence.Eligibility.Should().Be(PromotionEligibility.Verified);
        result.Evidence.Actor.Should().Be("operator@example.com");
        result.Evidence.ApprovalKind.Should().Be(PromotionApprovalKind.UserConfirmation);
        result.Evidence.BaselineCommit.Should().Be(evidence.Baseline.Commit);
        result.Evidence.DiffHash.Should().Be(evidence.CandidateChangeSet.DiffHash);
        await fixture.AssertCandidateAppliedAsync(evidence.CandidateChangeSet.Diff);

        var persisted = await fixture.Store.LoadAsync(evidence.Id, CancellationToken.None);
        persisted!.Promotions.Should().ContainSingle(record =>
            record.Id == result.Evidence.Id &&
            record.Status == CandidatePromotionStatus.Promoted);
    }

    [Fact]
    public async Task Promotion_WithoutExplicitApproval_IsRejectedWithoutChangingRepository()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync();
        var request = fixture.Request(evidence, new PromotionApproval());

        var result = await fixture.Service.PromoteAsync(request, CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Rejected);
        result.Message.Should().Contain("confirmation is required");
        await fixture.AssertBaselineUnchangedAsync();
        (await fixture.LoadPromotionsAsync(evidence.Id)).Should().ContainSingle(record =>
            record.Status == CandidatePromotionStatus.Rejected &&
            record.ApprovalKind == PromotionApprovalKind.None);
    }

    [Fact]
    public async Task RejectedCandidate_CannotBePromoted()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync(TaskDecision.Rejected);

        var result = await fixture.Service.PromoteAsync(
            fixture.Request(evidence),
            CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Rejected);
        result.Message.Should().Contain("Only Verified or HumanReviewApproved");
        await fixture.AssertBaselineUnchangedAsync();
    }

    [Fact]
    public async Task HumanReviewCandidate_RequiresReferencedHumanApproval()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync(TaskDecision.HumanReviewRequired);
        var userConfirmation = await fixture.Service.PromoteAsync(
            fixture.Request(evidence),
            CancellationToken.None);

        userConfirmation.Status.Should().Be(CandidatePromotionStatus.Rejected);
        userConfirmation.Message.Should().Contain("HumanReview approval");

        var result = await fixture.Service.PromoteAsync(
            fixture.Request(evidence, new PromotionApproval
            {
                Kind = PromotionApprovalKind.HumanReview,
                Reference = "review/CR-1842"
            }),
            CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Promoted);
        result.Evidence.Eligibility.Should().Be(PromotionEligibility.HumanReviewApproved);
        result.Evidence.ApprovalReference.Should().Be("review/CR-1842");
        await fixture.AssertCandidateAppliedAsync(evidence.CandidateChangeSet.Diff);
    }

    [Fact]
    public async Task MismatchedConfirmedHash_IsRejected()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync();
        var request = new CandidatePromotionRequest
        {
            EvidenceId = evidence.Id,
            RepositoryPath = fixture.RepositoryPath,
            ExpectedDiffHash = "sha256:" + new string('0', 64),
            Actor = "operator@example.com",
            Approval = new PromotionApproval { Kind = PromotionApprovalKind.UserConfirmation }
        };

        var result = await fixture.Service.PromoteAsync(request, CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Rejected);
        result.Message.Should().Contain("Confirmed diff hash");
        await fixture.AssertBaselineUnchangedAsync();
    }

    [Fact]
    public async Task DivergedBaselineCommit_IsRejected()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync();
        await File.WriteAllTextAsync(
            Path.Combine(fixture.RepositoryPath, "after-baseline.txt"),
            "independent change\n");
        await fixture.GitAsync("add", "-A", "--");
        await fixture.GitAsync("commit", "-m", "independent change");

        var result = await fixture.Service.PromoteAsync(
            fixture.Request(evidence),
            CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Rejected);
        result.Message.Should().Contain("Repository HEAD changed from baseline");
        File.Exists(Path.Combine(fixture.RepositoryPath, "after-baseline.txt")).Should().BeTrue();
        (await fixture.StatusAsync()).Should().BeEmpty();
    }

    [Fact]
    public async Task DirtyOriginalRepository_IsRejectedAndPreserved()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync();
        var dirtyFile = Path.Combine(fixture.RepositoryPath, "operator-work.txt");
        await File.WriteAllTextAsync(dirtyFile, "do not overwrite\n");

        var result = await fixture.Service.PromoteAsync(
            fixture.Request(evidence),
            CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Rejected);
        result.Message.Should().Contain("working tree or index changed");
        (await File.ReadAllTextAsync(dirtyFile)).Should().Be("do not overwrite\n");
    }

    [Fact]
    public async Task ExportPatch_WritesOutsideRepositoryWithoutApplyingCandidate()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync();
        var outputPath = Path.Combine(fixture.RootPath, "exports", "candidate.patch");

        var result = await fixture.Service.ExportPatchAsync(new CandidatePatchExportRequest
        {
            EvidenceId = evidence.Id,
            DestinationPath = outputPath,
            ExpectedDiffHash = evidence.CandidateChangeSet.DiffHash,
            Actor = "operator@example.com"
        }, CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Exported);
        (await File.ReadAllTextAsync(outputPath)).Should().Be(evidence.CandidateChangeSet.Diff);
        await fixture.AssertBaselineUnchangedAsync();
        (await fixture.LoadPromotionsAsync(evidence.Id)).Should().ContainSingle(record =>
            record.Action == CandidatePromotionAction.ExportPatch &&
            record.Status == CandidatePromotionStatus.Exported &&
            record.OutputPath == outputPath);
    }

    [Fact]
    public async Task ConcurrentPromotions_ApplyCandidateExactlyOnce()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync();

        var results = await Task.WhenAll(
            fixture.Service.PromoteAsync(fixture.Request(evidence), CancellationToken.None),
            fixture.Service.PromoteAsync(fixture.Request(evidence), CancellationToken.None));

        results.Should().ContainSingle(result => result.Status == CandidatePromotionStatus.Promoted);
        results.Should().ContainSingle(result => result.Status == CandidatePromotionStatus.Rejected);
        await fixture.AssertCandidateAppliedAsync(evidence.CandidateChangeSet.Diff);
        (await fixture.LoadPromotionsAsync(evidence.Id)).Should().HaveCount(2);
    }

    [Fact]
    public async Task MultiFilePatchWithInvalidSecondHunk_AppliesNothing()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync(
            transformDiff: diff => diff.Replace(
                "-two baseline",
                "-two content that cannot match",
                StringComparison.Ordinal));

        var result = await fixture.Service.PromoteAsync(
            fixture.Request(evidence),
            CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Rejected);
        result.Message.Should().Contain("Patch preflight failed without applying changes");
        await fixture.AssertBaselineUnchangedAsync();
    }

    [Fact]
    public async Task EvidencePersistenceFailure_RollsBackAppliedPatch()
    {
        await using var fixture = await PromotionFixture.CreateAsync();
        var evidence = await fixture.SaveEvidenceAsync();
        var service = new CandidatePromotionService(
            fixture.ProcessRunner,
            new AppendFailingEvidenceStore(fixture.Store));

        var result = await service.PromoteAsync(
            fixture.Request(evidence),
            CancellationToken.None);

        result.Status.Should().Be(CandidatePromotionStatus.Failed);
        result.Message.Should().Contain("repository was rolled back");
        await fixture.AssertBaselineUnchangedAsync();
    }

    private sealed class AppendFailingEvidenceStore : IExecutionEvidenceStore
    {
        private readonly IExecutionEvidenceStore _inner;

        public AppendFailingEvidenceStore(IExecutionEvidenceStore inner)
        {
            _inner = inner;
        }

        public void EnsureRepositoryIsolation(string repositoryPath) =>
            _inner.EnsureRepositoryIsolation(repositoryPath);

        public Task<string> SaveAsync(
            ExecutionEvidence evidence,
            CancellationToken cancellationToken) => _inner.SaveAsync(evidence, cancellationToken);

        public Task<ExecutionEvidence?> LoadAsync(
            Guid evidenceId,
            CancellationToken cancellationToken) => _inner.LoadAsync(evidenceId, cancellationToken);

        public Task AppendPromotionAsync(
            Guid evidenceId,
            CandidatePromotionEvidence promotion,
            CancellationToken cancellationToken) => throw new IOException("simulated evidence failure");
    }

    private sealed class PromotionFixture : IAsyncDisposable
    {
        private const string TemporaryRootPrefix = "aecs-promotion-tests-";
        private readonly string _baselineCommit;
        private readonly string _baselineBranch;
        private bool _disposed;

        private PromotionFixture(string rootPath, string baselineCommit, string baselineBranch)
        {
            RootPath = rootPath;
            RepositoryPath = Path.Combine(rootPath, "repository");
            EvidencePath = Path.Combine(rootPath, "evidence");
            _baselineCommit = baselineCommit;
            _baselineBranch = baselineBranch;
            Store = new JsonExecutionEvidenceStore(EvidencePath);
            Service = new CandidatePromotionService(ProcessRunner, Store);
        }

        public string RootPath { get; }
        public string RepositoryPath { get; }
        public string EvidencePath { get; }
        public SystemProcessRunner ProcessRunner { get; } = new();
        public JsonExecutionEvidenceStore Store { get; }
        public CandidatePromotionService Service { get; }

        public static async Task<PromotionFixture> CreateAsync()
        {
            var rootPath = Path.Combine(
                Path.GetTempPath(),
                $"{TemporaryRootPrefix}{Guid.NewGuid():N}");
            var repositoryPath = Path.Combine(rootPath, "repository");
            Directory.CreateDirectory(repositoryPath);
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "one.txt"), "one baseline\n");
            await File.WriteAllTextAsync(Path.Combine(repositoryPath, "two.txt"), "two baseline\n");

            var runner = new SystemProcessRunner();
            await RequiredGitAsync(runner, repositoryPath, "init", "--initial-branch=main");
            await RequiredGitAsync(
                runner,
                repositoryPath,
                "config", "user.email", "aecs-tests@example.invalid");
            await RequiredGitAsync(runner, repositoryPath, "config", "user.name", "AECS Tests");
            await RequiredGitAsync(runner, repositoryPath, "config", "core.autocrlf", "false");
            await RequiredGitAsync(runner, repositoryPath, "add", "-A", "--");
            await RequiredGitAsync(runner, repositoryPath, "commit", "-m", "baseline");
            var commit = (await RequiredGitAsync(runner, repositoryPath, "rev-parse", "HEAD"))
                .StandardOutput.Trim();
            var branch = (await RequiredGitAsync(
                    runner,
                    repositoryPath,
                    "rev-parse", "--abbrev-ref", "HEAD"))
                .StandardOutput.Trim();
            return new PromotionFixture(rootPath, commit, branch);
        }

        public async Task<ExecutionEvidence> SaveEvidenceAsync(
            TaskDecision decision = TaskDecision.Verified,
            Func<string, string>? transformDiff = null)
        {
            await File.WriteAllTextAsync(Path.Combine(RepositoryPath, "one.txt"), "one promoted\n");
            await File.WriteAllTextAsync(Path.Combine(RepositoryPath, "two.txt"), "two promoted\n");
            await GitAsync("add", "-A", "--");
            var diff = (await GitAsync(
                    "diff", "--cached", "--binary", "--no-ext-diff", _baselineCommit, "--"))
                .StandardOutput;
            await GitAsync("reset", "--hard", _baselineCommit);
            diff = transformDiff?.Invoke(diff) ?? diff;

            var taskId = "TASK-PROMOTION-001";
            var agentRunId = Guid.NewGuid();
            var state = decision switch
            {
                TaskDecision.Verified => TaskState.Verified,
                TaskDecision.HumanReviewRequired => TaskState.HumanReviewRequired,
                _ => TaskState.Rejected
            };
            var evidence = new ExecutionEvidence
            {
                TaskContract = new TaskContract { Id = taskId, Objective = "Promote candidate" },
                AgentRun = new AgentRun { Id = agentRunId, TaskId = taskId },
                AgentResult = new AgentRunResult { Success = true },
                Baseline = new BaselineSnapshot
                {
                    Commit = _baselineCommit,
                    Branch = _baselineBranch,
                    GitStatus = string.Empty,
                    RepositoryPath = RepositoryPath
                },
                CandidateChangeSet = new CandidateChangeSet
                {
                    TaskId = taskId,
                    AgentRunId = agentRunId.ToString("N"),
                    BaselineCommit = _baselineCommit,
                    ModifiedFiles = ["one.txt", "two.txt"],
                    Diff = diff,
                    DiffHash = Hash(diff)
                },
                FinalDecision = new FinalDecisionRecord
                {
                    Decision = decision,
                    State = state,
                    Reason = "test fixture"
                }
            };
            await Store.SaveAsync(evidence, CancellationToken.None);
            return evidence;
        }

        public CandidatePromotionRequest Request(
            ExecutionEvidence evidence,
            PromotionApproval? approval = null) => new()
            {
                EvidenceId = evidence.Id,
                RepositoryPath = RepositoryPath,
                ExpectedDiffHash = evidence.CandidateChangeSet.DiffHash,
                Actor = "operator@example.com",
                Approval = approval ?? new PromotionApproval
                {
                    Kind = PromotionApprovalKind.UserConfirmation
                }
            };

        public async Task AssertCandidateAppliedAsync(string expectedDiff)
        {
            (await File.ReadAllTextAsync(Path.Combine(RepositoryPath, "one.txt")))
                .Should().Be("one promoted\n");
            (await File.ReadAllTextAsync(Path.Combine(RepositoryPath, "two.txt")))
                .Should().Be("two promoted\n");
            var stagedDiff = (await GitAsync(
                    "diff", "--cached", "--binary", "--no-ext-diff", _baselineCommit, "--"))
                .StandardOutput;
            stagedDiff.Should().Be(expectedDiff);
        }

        public async Task AssertBaselineUnchangedAsync()
        {
            (await GitAsync("rev-parse", "HEAD")).StandardOutput.Trim().Should().Be(_baselineCommit);
            (await StatusAsync()).Should().BeEmpty();
            (await File.ReadAllTextAsync(Path.Combine(RepositoryPath, "one.txt")))
                .Should().Be("one baseline\n");
            (await File.ReadAllTextAsync(Path.Combine(RepositoryPath, "two.txt")))
                .Should().Be("two baseline\n");
        }

        public async Task<List<CandidatePromotionEvidence>> LoadPromotionsAsync(Guid evidenceId) =>
            (await Store.LoadAsync(evidenceId, CancellationToken.None))!.Promotions;

        public async Task<string> StatusAsync() =>
            (await GitAsync("status", "--porcelain=v1", "--untracked-files=all"))
            .StandardOutput;

        public Task<ProcessExecutionResult> GitAsync(params string[] arguments) =>
            RequiredGitAsync(ProcessRunner, RepositoryPath, arguments);

        public ValueTask DisposeAsync()
        {
            if (_disposed)
                return ValueTask.CompletedTask;
            _disposed = true;

            var resolvedRoot = Path.GetFullPath(RootPath);
            var temporaryRoot = Path.GetFullPath(Path.GetTempPath())
                .TrimEnd(Path.DirectorySeparatorChar);
            if (!resolvedRoot.StartsWith(
                    temporaryRoot + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(resolvedRoot).StartsWith(
                    TemporaryRootPrefix,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    $"Refusing to delete unexpected fixture path: {resolvedRoot}");
            }

            if (Directory.Exists(resolvedRoot))
            {
                foreach (var file in Directory.EnumerateFiles(
                             resolvedRoot,
                             "*",
                             SearchOption.AllDirectories))
                {
                    File.SetAttributes(file, FileAttributes.Normal);
                }

                Directory.Delete(resolvedRoot, recursive: true);
            }

            return ValueTask.CompletedTask;
        }

        private static async Task<ProcessExecutionResult> RequiredGitAsync(
            IProcessRunner runner,
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
            if (!result.Succeeded)
            {
                throw new InvalidOperationException(
                    $"git {string.Join(' ', arguments)} failed with {result.ExitCode}: " +
                    result.StandardError);
            }

            return result;
        }

        private static string Hash(string value)
        {
            var digest = SHA256.HashData(Encoding.UTF8.GetBytes(value));
            return $"sha256:{Convert.ToHexString(digest).ToLowerInvariant()}";
        }
    }
}
