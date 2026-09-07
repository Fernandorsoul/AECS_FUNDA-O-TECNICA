using System.Security.Cryptography;
using AECS.Application.AdaptiveController;
using AECS.Application.Experiments;
using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Processes;
using FluentAssertions;

namespace AECS.IntegrationTests;

public sealed class AdaptiveOfflineTaskBaselineVerifierTests
{
    [Fact]
    public async Task VerifyAsync_AcceptsMultipleRepositoriesAndPreservesSourceCheckouts()
    {
        await using var fixture = await MultiRepositoryFixture.CreateAsync();
        var verifier = new AdaptiveOfflineTaskBaselineVerifier(fixture.ProcessRunner);
        var firstTask = fixture.Dataset.Manifest.Tasks[0];
        var secondTask = fixture.Dataset.Manifest.Tasks[1];
        var before = await fixture.SnapshotsAsync();

        var first = await verifier.VerifyAsync(
            fixture.Dataset,
            firstTask,
            CancellationToken.None);
        var second = await verifier.VerifyAsync(
            fixture.Dataset,
            secondTask,
            CancellationToken.None);

        first.RepositoryId.Should().Be("repo-a");
        second.RepositoryId.Should().Be("repo-b");
        first.BaselineCommit.Should().NotBe(second.BaselineCommit);
        first.OracleDiffHash.Should().Be(firstTask.Oracle!.DiffHash);
        second.OracleDiffHash.Should().Be(secondTask.Oracle!.DiffHash);
        (await fixture.SnapshotsAsync()).Should().Equal(before);
    }

    [Fact]
    public async Task VerifyAsync_RejectsTamperedOracleWithoutChangingSourceCheckout()
    {
        await using var fixture = await MultiRepositoryFixture.CreateAsync();
        var verifier = new AdaptiveOfflineTaskBaselineVerifier(fixture.ProcessRunner);
        var original = fixture.Dataset.Manifest.Tasks[0];
        var tampered = fixture.CopyTask(original, "sha256:" + new string('0', 64));
        var before = await fixture.SnapshotsAsync();

        var action = () => verifier.VerifyAsync(
            fixture.Dataset,
            tampered,
            CancellationToken.None);

        await action.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*oracle diff hash*");
        (await fixture.SnapshotsAsync()).Should().Equal(before);
    }

    [Fact]
    public async Task Runner_PersistsV2ProvenanceAndFailsBeforeProviderWhenEvidenceIsMissing()
    {
        await using var fixture = await MultiRepositoryFixture.CreateAsync();
        var evidence = new MissingEvidenceStore();
        var executor = new RecordingExecutor();
        var runner = new AdaptiveOfflineRunner(
            new AdaptiveOfflinePreflight(evidence, evidence),
            executor,
            new AdaptiveOfflineTaskBaselineVerifier(fixture.ProcessRunner));

        var artifacts = new AdaptiveOfflineArtifactStore(Path.Combine(fixture.Root, "output"));
        var report = await runner.RunAsync(
            fixture.Dataset,
            artifacts,
            resume: false,
            CancellationToken.None);

        executor.Calls.Should().Be(0);
        report.SchemaVersion.Should().Be(AdaptiveOfflineSchema.MultiBaselineReportVersion);
        report.DatasetSchemaVersion.Should().Be(
            AdaptiveOfflineSchema.MultiBaselineDatasetVersion);
        report.Pairs.Should().HaveCount(50).And.OnlyContain(pair =>
            pair.Status == AdaptiveOfflineResultStatus.Failed &&
            pair.RepositoryId.Length > 0 &&
            pair.SourceUri.StartsWith("https://", StringComparison.Ordinal) &&
            pair.LicenseSpdx == "MIT" &&
            pair.UpstreamCommit.Length == 40 &&
            pair.BaselineCommit.Length == 40 &&
            pair.OracleDiffHash.StartsWith("sha256:", StringComparison.Ordinal));

        File.WriteAllText(
            Path.Combine(fixture.Dataset.RepositoryPaths["repo-a"], "tampered.txt"),
            "tamper");
        var resume = () => runner.RunAsync(
            fixture.Dataset,
            artifacts,
            resume: true,
            CancellationToken.None);
        await resume.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*dirty repository*");
        executor.Calls.Should().Be(0);
    }

    private sealed class MultiRepositoryFixture : IAsyncDisposable
    {
        private const string Image = SandboxExecutionProfile.DefaultImage;

        private MultiRepositoryFixture(string root, IProcessRunner processRunner)
        {
            Root = root;
            ProcessRunner = processRunner;
        }

        public string Root { get; }
        public IProcessRunner ProcessRunner { get; }
        public LoadedAdaptiveOfflineDataset Dataset { get; private set; } = new();

        public static async Task<MultiRepositoryFixture> CreateAsync()
        {
            var root = Path.Combine(
                Path.GetTempPath(),
                "aecs-adaptive-baseline-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            var fixture = new MultiRepositoryFixture(root, new SystemProcessRunner());
            var first = await fixture.CreateRepositoryAsync(
                "repo-a",
                "https://example.invalid/owner/a.git",
                "alpha");
            var second = await fixture.CreateRepositoryAsync(
                "repo-b",
                "https://example.invalid/owner/b.git",
                "beta");
            var repositories = new[] { first, second };
            var tasks = Enumerable.Range(1, 50).Select(index =>
            {
                var repository = repositories[(index - 1) % repositories.Length];
                var id = $"REAL-{index:000}";
                var contractsDirectory = Path.Combine(root, "contracts");
                Directory.CreateDirectory(contractsDirectory);
                var contractPath = Path.Combine(contractsDirectory, id + ".yaml");
                File.WriteAllText(contractPath, ContractYaml(id));
                return new AdaptiveOfflineTaskDefinition
                {
                    Id = id,
                    ContractPath = $"contracts/{id}.yaml",
                    ContractHash = HashFile(contractPath),
                    ExpectedDecision = TaskDecision.Verified,
                    Seed = 9000 + index,
                    HistoryCutoffUtc = new DateTime(2026, 9, 3, 0, 0, 0, DateTimeKind.Utc),
                    RecommendationEvidenceId = Guid.NewGuid(),
                    RecommendationEvidenceHash = "sha256:" + new string('e', 64),
                    RepositoryId = repository.Id,
                    UpstreamCommit = repository.UpstreamCommit,
                    BaselineCommit = repository.BaselineCommit,
                    Oracle = new AdaptiveOfflineTaskOracle
                    {
                        Kind = AdaptiveOfflineOracleKind.PrecommittedTestPatch,
                        DiffHash = repository.DiffHash,
                        AllowedPaths = ["tests/**"],
                        ContainerImage = Image,
                        FailToPass = [$"regression_{index}"],
                        PassToPass = [$"existing_{index}"]
                    }
                };
            }).ToList();
            var manifest = new AdaptiveOfflineDatasetManifest
            {
                SchemaVersion = AdaptiveOfflineSchema.MultiBaselineDatasetVersion,
                Id = "multi-repository-fixture",
                Version = "2.0.0",
                PreregisteredAtUtc = new DateTime(2026, 9, 4, 0, 0, 0, DateTimeKind.Utc),
                Repositories = repositories.Select(repository =>
                    new AdaptiveOfflineRepositoryCatalogEntry
                    {
                        Id = repository.Id,
                        Path = repository.Id,
                        SourceUri = repository.SourceUri,
                        LicenseSpdx = "MIT",
                        RedistributionAllowed = true
                    }).ToList(),
                Provider = new AdaptiveOfflineProviderDefinition
                {
                    Kind = AdaptiveOfflineProvider.Local,
                    Parameters = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["baseUrl"] = "http://localhost:11434",
                        ["contextWindowTokens"] = "8192"
                    }
                },
                Repetitions = 1,
                Protocol = Protocol(),
                Tasks = tasks
            };
            fixture.Dataset = new LoadedAdaptiveOfflineDataset
            {
                ManifestPath = Path.Combine(root, "dataset.json"),
                ManifestDirectory = root,
                RepositoryPaths = repositories.ToDictionary(
                    repository => repository.Id,
                    repository => repository.Path,
                    StringComparer.Ordinal),
                Manifest = manifest
            };
            return fixture;
        }

        public AdaptiveOfflineTaskDefinition CopyTask(
            AdaptiveOfflineTaskDefinition source,
            string diffHash) => new()
            {
                Id = source.Id,
                ContractPath = source.ContractPath,
                ExpectedDecision = source.ExpectedDecision,
                Seed = source.Seed,
                HistoryCutoffUtc = source.HistoryCutoffUtc,
                RecommendationEvidenceId = source.RecommendationEvidenceId,
                RecommendationEvidenceHash = source.RecommendationEvidenceHash,
                ContractHash = source.ContractHash,
                RepositoryId = source.RepositoryId,
                UpstreamCommit = source.UpstreamCommit,
                BaselineCommit = source.BaselineCommit,
                Oracle = new AdaptiveOfflineTaskOracle
                {
                    Kind = source.Oracle!.Kind,
                    DiffHash = diffHash,
                    AllowedPaths = source.Oracle.AllowedPaths,
                    ContainerImage = source.Oracle.ContainerImage,
                    FailToPass = source.Oracle.FailToPass,
                    PassToPass = source.Oracle.PassToPass
                }
            };

        public async Task<List<string>> SnapshotsAsync()
        {
            var snapshots = new List<string>();
            foreach (var path in Dataset.RepositoryPaths.OrderBy(item => item.Key))
            {
                var head = await GitAsync(path.Value, "rev-parse", "HEAD");
                var status = await GitAsync(
                    path.Value,
                    "status",
                    "--porcelain=v1",
                    "--untracked-files=all");
                snapshots.Add(path.Key + ":" + head.StandardOutput.Trim() + ":" +
                    status.StandardOutput);
            }
            return snapshots;
        }

        public ValueTask DisposeAsync()
        {
            if (Directory.Exists(Root))
            {
                foreach (var file in Directory.EnumerateFiles(Root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(Root, recursive: true);
            }
            return ValueTask.CompletedTask;
        }

        private async Task<RepositoryFixture> CreateRepositoryAsync(
            string id,
            string sourceUri,
            string value)
        {
            var path = Path.Combine(Root, id);
            Directory.CreateDirectory(path);
            await GitAsync(path, "init", "--initial-branch=main");
            await GitAsync(path, "config", "user.email", "aecs@example.invalid");
            await GitAsync(path, "config", "user.name", "AECS Test");
            await GitAsync(path, "config", "core.autocrlf", "false");
            await GitAsync(path, "remote", "add", "origin", sourceUri);
            Directory.CreateDirectory(Path.Combine(path, "src"));
            await File.WriteAllTextAsync(Path.Combine(path, "src", "value.txt"), value + "\n");
            await GitAsync(path, "add", "-A", "--");
            await GitAsync(path, "commit", "-m", "upstream baseline");
            var upstream = (await GitAsync(path, "rev-parse", "HEAD")).StandardOutput.Trim();
            Directory.CreateDirectory(Path.Combine(path, "tests"));
            await File.WriteAllTextAsync(
                Path.Combine(path, "tests", "oracle.txt"),
                "expected:" + value + "\n");
            await GitAsync(path, "add", "-A", "--");
            await GitAsync(path, "commit", "-m", "precommitted test oracle");
            var baseline = (await GitAsync(path, "rev-parse", "HEAD")).StandardOutput.Trim();
            var diff = (await GitAsync(
                path,
                "diff",
                "--binary",
                "--no-ext-diff",
                upstream,
                baseline,
                "--")).StandardOutput;
            return new RepositoryFixture(
                id,
                path,
                sourceUri,
                upstream,
                baseline,
                AdaptiveOfflineTaskBaselineVerifier.HashDiff(diff));
        }

        private async Task<ProcessExecutionResult> GitAsync(
            string workingDirectory,
            params string[] arguments)
        {
            var result = await ProcessRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "git",
                Arguments = arguments,
                WorkingDirectory = workingDirectory,
                Timeout = TimeSpan.FromSeconds(30)
            }, CancellationToken.None);
            result.Succeeded.Should().BeTrue(result.StandardError);
            return result;
        }

        private static AdaptiveOfflineProtocol Protocol() => new()
        {
            Design = AdaptiveOfflineSchema.Design,
            HypothesisId = AdaptiveOfflineSchema.HypothesisId,
            Hypothesis = "Bounded adaptive routing improves verified changes per cost.",
            PrimaryMetric = AdaptiveOfflineSchema.PrimaryMetric,
            MinimumDistinctTasks = 50,
            ConfidenceLevel = 0.95,
            ZeroReferencePolicy = ZeroReferencePolicy.AbsolutePairedDelta,
            DeathCriteria = new AdaptiveOfflineDeathCriteria
            {
                MinimumRelativeImprovement = 0.1,
                MinimumAbsoluteImprovement = 1,
                MaximumCandidateFailureRate = 0.05,
                MaximumCandidateScopeViolationRate = 0,
                MaximumCandidateSecurityViolationRate = 0,
                MaximumMedianLatencyRegressionRate = 0.25
            }
        };

        private static string ContractYaml(string id) => $$"""
            schema_version: aecs.task-contract/v1
            task:
              id: {{id}}
              objective: Fix a real regression using the pinned test oracle
              acceptance: []
              scope:
                allowed:
                  - src/**
                forbidden:
                  - tests/**
              constraints:
                security_risk: low
                database_migration: false
                external_dependency: false
              budget:
                tokens: 5000
                usd: 0.01
                retries: 0
                wall_clock_seconds: 30
                max_files_changed: 2
              execution:
                runtime: docker
                working_directory: .
                sandbox:
                  image: {{Image}}
                  cpu_limit: "1.0"
                  memory_limit: 512m
                  process_limit: 128
                  wall_clock_seconds: 30
                  network_access: false
              verification:
                build: disabled
                unit_tests: required
                integration_tests: disabled
                scope: required
                security_scan: required
                architecture: disabled
                critical_semantic_failures: disabled
              approval:
                production: none
            """;

        private static string HashFile(string path) =>
            "sha256:" + Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)))
                .ToLowerInvariant();

        private sealed record RepositoryFixture(
            string Id,
            string Path,
            string SourceUri,
            string UpstreamCommit,
            string BaselineCommit,
            string DiffHash);
    }

    private sealed class RecordingExecutor : IAdaptiveOfflineArmExecutor
    {
        public int Calls { get; private set; }

        public Task<StagedExecutionResult> ExecuteAsync(
            AdaptiveOfflineArmExecutionRequest request,
            CancellationToken cancellationToken)
        {
            Calls++;
            throw new InvalidOperationException("Provider must not execute.");
        }
    }

    private sealed class MissingEvidenceStore : IExecutionEvidenceStore, IEvidenceGraphSource
    {
        public void EnsureRepositoryIsolation(string repositoryPath)
        {
        }

        public Task<ExecutionEvidence?> LoadAsync(
            Guid evidenceId,
            CancellationToken cancellationToken) => Task.FromResult<ExecutionEvidence?>(null);

        public Task<EvidenceGraph?> LoadEvidenceGraphAsync(
            Guid evidenceId,
            EvidenceReadScope scope,
            CancellationToken cancellationToken) => Task.FromResult<EvidenceGraph?>(null);

        public Task<EvidenceGraphQueryResult> QueryEvidenceGraphsAsync(
            EvidenceGraphQuery query,
            EvidenceReadScope scope,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task<string> SaveAsync(
            ExecutionEvidence evidence,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AppendPromotionAsync(
            Guid evidenceId,
            CandidatePromotionEvidence promotion,
            CancellationToken cancellationToken) => throw new NotSupportedException();

        public Task AppendReplayAsync(
            Guid evidenceId,
            ExecutionReplayEvidence replay,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
