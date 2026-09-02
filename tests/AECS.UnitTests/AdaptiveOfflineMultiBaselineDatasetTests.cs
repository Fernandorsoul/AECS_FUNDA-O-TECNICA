using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using AECS.Application.AdaptiveController;
using AECS.Application.Experiments;
using AECS.Domain.Enums;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class AdaptiveOfflineMultiBaselineDatasetTests
{
    [Fact]
    public void Contract_AcceptsFiftyTasksAcrossMultiplePinnedRepositories()
    {
        using var fixture = new MultiBaselineFixture();

        var action = () => AdaptiveOfflineDatasetContract.Validate(fixture.Manifest);

        action.Should().NotThrow();
        AdaptiveOfflineDatasetFingerprint.Create(fixture.Manifest)
            .Should().StartWith("sha256:").And.HaveLength(71);
    }

    [Fact]
    public void Loader_ResolvesContractsOutsideEvaluatedRepositories()
    {
        using var fixture = new MultiBaselineFixture();

        var loaded = AdaptiveOfflineDatasetLoader.Load(fixture.ManifestPath);

        loaded.IsMultiBaseline.Should().BeTrue();
        loaded.RepositoryPaths.Should().HaveCount(2);
        loaded.ContractPath(loaded.Manifest.Tasks[0]).Should()
            .StartWith(Path.Combine(fixture.Root, "contracts"));
        loaded.ContractPath(loaded.Manifest.Tasks[0]).Should()
            .NotStartWith(loaded.RepositoryPathFor(loaded.Manifest.Tasks[0]));
    }

    [Fact]
    public void Contract_RejectsUnknownRepositoryReference()
    {
        using var fixture = new MultiBaselineFixture();
        var manifest = fixture.Copy(tasks: fixture.Manifest.Tasks.Select((task, index) =>
            index == 0 ? fixture.CopyTask(task, repositoryId: "missing") : task).ToList());

        var action = () => AdaptiveOfflineDatasetContract.Validate(manifest);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Contract_RejectsOraclePathTraversal()
    {
        using var fixture = new MultiBaselineFixture();
        var first = fixture.Manifest.Tasks[0];
        var invalidOracle = fixture.CopyOracle(first.Oracle!, ["../gold.patch"]);
        var manifest = fixture.Copy(tasks: fixture.Manifest.Tasks.Select((task, index) =>
            index == 0 ? fixture.CopyTask(task, oracle: invalidOracle) : task).ToList());

        var action = () => AdaptiveOfflineDatasetContract.Validate(manifest);

        action.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Loader_RejectsGoldPatchField()
    {
        using var fixture = new MultiBaselineFixture();
        var json = JsonNode.Parse(JsonSerializer.Serialize(
            fixture.Manifest,
            AdaptiveOfflineDatasetLoader.SerializerOptions))!.AsObject();
        json["tasks"]!.AsArray()[0]!.AsObject()["goldPatch"] = "forbidden.diff";
        File.WriteAllText(fixture.ManifestPath, json.ToJsonString());

        var action = () => AdaptiveOfflineDatasetLoader.Load(fixture.ManifestPath);

        action.Should().Throw<JsonException>();
    }

    [Fact]
    public void Loader_RejectsTaskContractChangedAfterPreregistration()
    {
        using var fixture = new MultiBaselineFixture();
        File.AppendAllText(
            Path.Combine(fixture.Root, fixture.Manifest.Tasks[0].ContractPath),
            "# tampered\n");

        var action = () => AdaptiveOfflineDatasetLoader.Load(fixture.ManifestPath);

        action.Should().Throw<InvalidOperationException>()
            .WithMessage("*does not match its preregistered hash*");
    }

    private sealed class MultiBaselineFixture : IDisposable
    {
        private static readonly DateTime Preregistered =
            new(2026, 9, 4, 12, 0, 0, DateTimeKind.Utc);

        public MultiBaselineFixture()
        {
            Root = Path.Combine(
                Path.GetTempPath(),
                "aecs-adaptive-v2-tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path.Combine(Root, "repos", "a"));
            Directory.CreateDirectory(Path.Combine(Root, "repos", "b"));
            Directory.CreateDirectory(Path.Combine(Root, "contracts"));
            var tasks = Enumerable.Range(1, 50).Select(index =>
            {
                var id = $"REAL-{index:000}";
                const string contract = "task: {}\n";
                File.WriteAllText(Path.Combine(Root, "contracts", id + ".yaml"), contract);
                return new AdaptiveOfflineTaskDefinition
                {
                    Id = id,
                    ContractPath = $"contracts/{id}.yaml",
                    ContractHash = HashContent(contract),
                    ExpectedDecision = TaskDecision.Verified,
                    Seed = 8000 + index,
                    HistoryCutoffUtc = Preregistered.AddDays(-2),
                    RecommendationEvidenceId = Guid.NewGuid(),
                    RecommendationEvidenceHash = Hash('e'),
                    RepositoryId = index % 2 == 0 ? "repo-b" : "repo-a",
                    UpstreamCommit = index % 2 == 0 ? new string('b', 40) : new string('a', 40),
                    BaselineCommit = index % 2 == 0 ? new string('d', 40) : new string('c', 40),
                    Oracle = new AdaptiveOfflineTaskOracle
                    {
                        Kind = AdaptiveOfflineOracleKind.PrecommittedTestPatch,
                        DiffHash = Hash('f'),
                        AllowedPaths = ["tests/**"],
                        ContainerImage = "example.invalid/aecs/test@" + Hash('1'),
                        FailToPass = [$"test_{index}_regression"],
                        PassToPass = [$"test_{index}_existing"]
                    }
                };
            }).ToList();
            Manifest = Copy(tasks: tasks);
            ManifestPath = Path.Combine(Root, "dataset.json");
            File.WriteAllText(ManifestPath, JsonSerializer.Serialize(
                Manifest,
                AdaptiveOfflineDatasetLoader.SerializerOptions));
        }

        public string Root { get; }
        public string ManifestPath { get; }
        public AdaptiveOfflineDatasetManifest Manifest { get; }

        public AdaptiveOfflineDatasetManifest Copy(
            List<AdaptiveOfflineTaskDefinition>? tasks = null) => new()
            {
                SchemaVersion = AdaptiveOfflineSchema.MultiBaselineDatasetVersion,
                Id = "adaptive-real-corpus",
                Version = "2.0.0",
                PreregisteredAtUtc = Preregistered,
                Repositories =
                [
                    Repository("repo-a", "repos/a", "https://example.invalid/a.git"),
                    Repository("repo-b", "repos/b", "https://example.invalid/b.git")
                ],
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
                Protocol = new AdaptiveOfflineProtocol
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
                },
                Tasks = tasks ?? Manifest.Tasks
            };

        public AdaptiveOfflineTaskDefinition CopyTask(
            AdaptiveOfflineTaskDefinition source,
            string? repositoryId = null,
            AdaptiveOfflineTaskOracle? oracle = null) => new()
            {
                Id = source.Id,
                ContractPath = source.ContractPath,
                ExpectedDecision = source.ExpectedDecision,
                Seed = source.Seed,
                HistoryCutoffUtc = source.HistoryCutoffUtc,
                RecommendationEvidenceId = source.RecommendationEvidenceId,
                RecommendationEvidenceHash = source.RecommendationEvidenceHash,
                ContractHash = source.ContractHash,
                RepositoryId = repositoryId ?? source.RepositoryId,
                UpstreamCommit = source.UpstreamCommit,
                BaselineCommit = source.BaselineCommit,
                Oracle = oracle ?? source.Oracle
            };

        public AdaptiveOfflineTaskOracle CopyOracle(
            AdaptiveOfflineTaskOracle source,
            List<string> allowedPaths) => new()
            {
                Kind = source.Kind,
                DiffHash = source.DiffHash,
                AllowedPaths = allowedPaths,
                ContainerImage = source.ContainerImage,
                FailToPass = source.FailToPass,
                PassToPass = source.PassToPass
            };

        public void Dispose()
        {
            if (Directory.Exists(Root))
                Directory.Delete(Root, recursive: true);
        }

        private static AdaptiveOfflineRepositoryCatalogEntry Repository(
            string id,
            string path,
            string sourceUri) => new()
            {
                Id = id,
                Path = path,
                SourceUri = sourceUri,
                LicenseSpdx = "MIT",
                RedistributionAllowed = true
            };

        private static string Hash(char value) => "sha256:" + new string(value, 64);

        private static string HashContent(string value) =>
            "sha256:" + Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    }
}
