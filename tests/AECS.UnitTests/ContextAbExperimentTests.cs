using AECS.Application.ContextCompiler;
using AECS.Application.Experiments;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;
using System.Text.Json;

namespace AECS.UnitTests;

public sealed class ContextAbExperimentTests
{
    [Theory]
    [InlineData("dataset.local-low-hardware-pilot.json", 2, 2, 1)]
    [InlineData("dataset.local-low-hardware.json", 10, 30, 3)]
    public void LocalLowHardwareDatasets_ArePreRegisteredSeparately(
        string fileName,
        int repetitions,
        int minimumPairedSamples,
        int taskCount)
    {
        var repository = FindRepositoryRoot();
        var dataset = ExperimentDatasetLoader.Load(Path.Combine(
            repository,
            "experiments",
            "context-compiler-h1",
            fileName));

        dataset.Manifest.Id.Should().StartWith("context-compiler-h1-local-low-hardware");
        dataset.Manifest.SchemaVersion.Should().Be(
            ExperimentDatasetSchema.ContextAbIntegrityVersion);
        dataset.Manifest.Version.Should().Be("2.0.0");
        dataset.Manifest.Repetitions.Should().Be(repetitions);
        dataset.Manifest.Tasks.Should().HaveCount(taskCount);
        dataset.Manifest.Tasks.Should().OnlyContain(task => task.RequiredContextPaths.Count == 2);
        dataset.Manifest.Protocol!.MinimumPairedSamples.Should().Be(minimumPairedSamples);
        dataset.Manifest.ReferenceVariantId.Should().Be("naive-local");
        dataset.Manifest.Variants.Should().HaveCount(2).And.OnlyContain(variant =>
            variant.Provider == ExperimentProvider.Local &&
            variant.Model == "qwen2.5-coder:1.5b" &&
            variant.RequiresRealProvider &&
            variant.Seed == 4200 &&
            variant.Parameters["baseUrl"] == "http://127.0.0.1:11434" &&
            variant.Parameters["contextWindowTokens"] == "8192");
    }

    [Theory]
    [InlineData("dataset.local-capacity-3b.json", "qwen2.5-coder:3b")]
    [InlineData("dataset.local-capacity-7b.json", "qwen2.5-coder:7b")]
    public void LocalModelCapacityDatasets_FixOneModelAndGraphContext(
        string fileName,
        string model)
    {
        var repository = FindRepositoryRoot();
        var dataset = ExperimentDatasetLoader.Load(Path.Combine(
            repository,
            "experiments",
            "context-compiler-h1",
            fileName));

        dataset.Manifest.SchemaVersion.Should().Be(ExperimentDatasetSchema.Version);
        dataset.Manifest.Protocol.Should().BeNull();
        dataset.Manifest.Version.Should().Be("2.0.0");
        dataset.Manifest.Repetitions.Should().Be(2);
        dataset.Manifest.Tasks.Should().HaveCount(3);
        dataset.Manifest.Tasks.Should().OnlyContain(task => task.RequiredContextPaths.Count == 2);
        dataset.Manifest.Variants.Should().ContainSingle().Which.Should().Match<ExperimentVariantDefinition>(
            variant => variant.Provider == ExperimentProvider.Local &&
                variant.Model == model &&
                variant.ContextStrategy == ContextStrategyIds.GraphRanked &&
                variant.RequiresRealProvider &&
                variant.Seed == 5200 &&
                variant.Parameters["baseUrl"] == "http://127.0.0.1:11434" &&
                variant.Parameters["contextWindowTokens"] == "8192");
    }

    [Fact]
    public void LocalModelCapacityGate_IsSequentialAndFailClosed()
    {
        var repository = FindRepositoryRoot();
        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            repository,
            "experiments",
            "context-compiler-h1",
            "local-model-capacity-gate.json")));
        var root = document.RootElement;
        var candidates = root.GetProperty("candidates").EnumerateArray().ToArray();
        var gate = root.GetProperty("gate");

        root.GetProperty("schemaVersion").GetString().Should()
            .Be("aecs.local-model-capacity-gate/v1");
        root.GetProperty("selectionPolicy").GetString().Should().Be("first-passing-candidate");
        root.GetProperty("failurePolicy").GetProperty("operationalFailure").GetString().Should()
            .Be("stop-and-diagnose");
        candidates.Select(candidate => candidate.GetProperty("model").GetString()).Should()
            .Equal("qwen2.5-coder:3b", "qwen2.5-coder:7b");
        candidates.Select(candidate => candidate.GetProperty("order").GetInt32()).Should()
            .Equal(1, 2);
        gate.GetProperty("requiredRuns").GetInt32().Should().Be(6);
        gate.GetProperty("minimumVerifiedChanges").GetInt32().Should().Be(3);
        gate.GetProperty("minimumVerifiedChangesPerTask").GetInt32().Should().Be(1);
        gate.GetProperty("maximumScopeViolations").GetInt32().Should().Be(0);
        gate.GetProperty("minimumFreePhysicalMemoryBytes").GetInt64().Should().Be(1L << 30);
        gate.GetProperty("requireIdempotentResume").GetBoolean().Should().BeTrue();
    }

    [Fact]
    public void RepositoryCompiler_ExecutesTheRequestedStrategyAndVersionsItsManifest()
    {
        var root = Directory.CreateTempSubdirectory("aecs-context-ab-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "src"));
            File.WriteAllText(
                Path.Combine(root.FullName, "src", "AaaDecoy.cs"),
                "namespace Demo; public class AaaDecoy { }");
            File.WriteAllText(
                Path.Combine(root.FullName, "src", "ZetaPolicy.cs"),
                "namespace Demo; public class ZetaPolicy { }");
            var contract = new AECS.Domain.Models.TaskContract
            {
                Id = "TASK-AB",
                Objective = "Change ZetaPolicy",
                Scope = new AECS.Domain.Models.ScopeDefinition { Allowed = ["src/**"] },
                Budget = new AECS.Domain.Models.ExecutionBudget { MaxTokens = 5000 }
            };

            var naive = new RepositoryContextCompiler(
                    selectionStrategy: ContextStrategyIds.NaivePathOrder)
                .Compile(root.FullName, contract, new string('a', 40));
            var graph = new RepositoryContextCompiler(
                    selectionStrategy: ContextStrategyIds.GraphRanked)
                .Compile(root.FullName, contract, new string('a', 40));

            naive.Manifest.StrategyVersion.Should().Be(ContextManifestSchema.NaiveStrategyVersion);
            naive.Manifest.Selections[0].Path.Should().Be("src/AaaDecoy.cs");
            graph.Manifest.StrategyVersion.Should().Be(ContextManifestSchema.StrategyVersion);
            graph.Manifest.Selections[0].Path.Should().Be("src/ZetaPolicy.cs");
            naive.Manifest.ManifestHash.Should().NotBe(graph.Manifest.ManifestHash);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }

    [Fact]
    public void VersionedBenchmark_HasARealContextContrastUnderTheSharedBudget()
    {
        var repository = FindRepositoryRoot();
        var benchmark = Path.Combine(
            repository,
            "experiments",
            "context-compiler-h1",
            "repository");
        var contract = new AECS.Domain.Models.TaskContract
        {
            Id = "H1-RETENTION",
            Objective = "Replace RetentionPolicy with the production value from RetentionContract",
            Scope = new AECS.Domain.Models.ScopeDefinition
            {
                Allowed = ["src/**"],
                Forbidden =
                [
                    "src/Policies/InvoicePolicy.cs",
                    "src/Policies/QueuePolicy.cs"
                ]
            },
            Budget = new AECS.Domain.Models.ExecutionBudget { MaxTokens = 8000 }
        };
        var options = new ContextCompilationOptions
        {
            MaxTokens = 1600,
            MaxCharacters = 4000,
            MaxFileCharacters = 400,
            MaxFileTokens = 200,
            DependencyDepth = 2
        };

        var naive = new RepositoryContextCompiler(
                defaultOptions: options,
                selectionStrategy: ContextStrategyIds.NaivePathOrder)
            .Compile(benchmark, contract, new string('a', 40));
        var compiled = new RepositoryContextCompiler(
                defaultOptions: options,
                selectionStrategy: ContextStrategyIds.GraphRanked)
            .Compile(benchmark, contract, new string('a', 40));

        naive.CodeContext.Should().NotContainKey("src/Policies/RetentionPolicy.cs");
        compiled.CodeContext.Should().ContainKey("src/Policies/RetentionPolicy.cs");
        compiled.CodeContext.Should().ContainKey("src/Contracts/RetentionContract.cs");
    }

    [Fact]
    public void NaiveSelection_UsesOnlyOrdinalPathsWhileGraphSelectionUsesTaskSignals()
    {
        var index = new CodebaseIndex
        {
            RootPath = ".",
            SourceFiles = ["src/AaaDecoy.cs", "src/ZetaPolicy.cs"],
            Symbols =
            [
                new CodeSymbol
                {
                    Name = "ZetaPolicy",
                    DisplayName = "Benchmark.ZetaPolicy",
                    FilePath = "src/ZetaPolicy.cs"
                }
            ],
            SemanticAuthority = true,
            Source = "roslyn-symbol-graph"
        };
        var selector = new ContextSelector();

        var naive = selector.Select(
            index,
            "TASK-AB",
            "Change ZetaPolicy",
            [],
            ["src/**"],
            [],
            new ContextSelectionOptions { Mode = ContextSelectionMode.NaivePathOrder });
        var graph = selector.Select(
            index,
            "TASK-AB",
            "Change ZetaPolicy",
            [],
            ["src/**"],
            [],
            new ContextSelectionOptions { Mode = ContextSelectionMode.GraphRanked });

        naive.RankedFiles.Select(file => file.Path).Should()
            .Equal("src/AaaDecoy.cs", "src/ZetaPolicy.cs");
        naive.SelectedSymbols.Should().BeEmpty();
        naive.Strategy.Should().Be(ContextStrategyIds.NaivePathOrder);
        graph.RankedFiles[0].Path.Should().Be("src/ZetaPolicy.cs");
        graph.SelectedSymbols.Should().Contain(symbol =>
            symbol.Contains("Benchmark.ZetaPolicy", StringComparison.Ordinal));
    }

    [Fact]
    public void ContextAbProtocol_RejectsAnyModelOrBudgetConfound()
    {
        var valid = Manifest();
        var validate = () => ExperimentDatasetContract.Validate(valid);
        validate.Should().NotThrow();

        var confounded = Manifest(candidateModel: "different-model");
        var reject = () => ExperimentDatasetContract.Validate(confounded);
        reject.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void ContextAbV3_RequiresRelevantContextPathsForEveryTask()
    {
        var valid = () => ExperimentDatasetContract.Validate(Manifest(integrity: true));
        var missing = () => ExperimentDatasetContract.Validate(Manifest(
            integrity: true,
            includeRequiredPaths: false));

        valid.Should().NotThrow();
        missing.Should().Throw<InvalidOperationException>();
    }

    [Fact]
    public void Analyzer_ReportsDistributionAndMaintainsH1OnlyForConclusivePairedEffect()
    {
        var manifest = Manifest();
        var results = new List<TaskExperimentResult>();
        var pairs = new List<ExperimentPairedComparison>();
        for (var repetition = 1; repetition <= 2; repetition++)
        {
            results.Add(Result("naive", repetition, cost: 0.10m));
            results.Add(Result("compiled", repetition, cost: 0.05m));
            pairs.Add(Pair(repetition, referenceCost: 0.10m, candidateCost: 0.05m));
        }

        var analysis = ExperimentAnalyzer.Analyze(manifest, results, pairs)!;

        analysis.Conclusion.Should().Be(HypothesisConclusion.Maintain);
        analysis.Pairs.ObservedPairs.Should().Be(2);
        analysis.Pairs.VccPerEstimatedDollarDelta.Count.Should().Be(2);
        analysis.Pairs.VccPerEstimatedDollarDelta.MeanConfidenceIntervalLower.Should()
            .BeGreaterThan(0);
        analysis.Variants.Should().OnlyContain(variant =>
            variant.VerifiedCodeChanges == 2 && variant.FirstPassVerifiedChanges == 2);
    }

    [Fact]
    public void Analyzer_DoesNotTreatZeroLocalCostAsInfiniteEfficiency()
    {
        var manifest = Manifest();
        var results = new List<TaskExperimentResult>();
        var pairs = new List<ExperimentPairedComparison>();
        for (var repetition = 1; repetition <= 2; repetition++)
        {
            results.Add(Result("naive", repetition, cost: 0));
            results.Add(Result("compiled", repetition, cost: 0));
            pairs.Add(Pair(repetition, referenceCost: 0, candidateCost: 0));
        }

        var analysis = ExperimentAnalyzer.Analyze(manifest, results, pairs)!;

        analysis.Conclusion.Should().Be(HypothesisConclusion.Adjust);
        analysis.Pairs.RelativePrimaryMetricImprovement.Should().BeNull();
        analysis.ConclusionReason.Should().Contain("cost-efficiency metric is unavailable");
    }

    private static ExperimentDatasetManifest Manifest(
        string candidateModel = "qwen-test",
        bool integrity = false,
        bool includeRequiredPaths = true) => new()
    {
        SchemaVersion = integrity
            ? ExperimentDatasetSchema.ContextAbIntegrityVersion
            : ExperimentDatasetSchema.ContextAbVersion,
        Id = "context-h1",
        Version = "1.0.0",
        Repository = new ExperimentRepositoryDefinition { Path = ".", Baseline = "HEAD" },
        Repetitions = 2,
        ReferenceVariantId = "naive",
        Protocol = new ExperimentProtocol
        {
            Design = ExperimentDesigns.PairedContextAb,
            HypothesisId = "H1",
            Hypothesis = "Selected context improves verified changes per estimated cost.",
            PrimaryMetric = ExperimentDesigns.VccPerEstimatedCost,
            MinimumPairedSamples = 2,
            ConfidenceLevel = 0.95,
            DeathCriteria = new ExperimentDeathCriteria
            {
                MinimumRelativeImprovement = 0.05,
                MaximumCandidateFailureRate = 0.25,
                MaximumCandidateScopeViolationRate = 0
            }
        },
        Tasks =
        [
            new ExperimentTaskDefinition
            {
                Id = "TASK-AB",
                ContractPath = "task.yaml",
                ExpectedDecision = TaskDecision.Verified,
                RequiredContextPaths = integrity && includeRequiredPaths
                    ? ["src/TargetPolicy.cs"]
                    : []
            }
        ],
        Variants =
        [
            Variant("naive", "qwen-test", ContextStrategyIds.NaivePathOrder),
            Variant("compiled", candidateModel, ContextStrategyIds.GraphRanked)
        ]
    };

    private static ExperimentVariantDefinition Variant(
        string id,
        string model,
        string strategy) => new()
        {
            Id = id,
            Provider = ExperimentProvider.Local,
            Model = model,
            ContextStrategy = strategy,
            Context = new ContextCompilationOptions
            {
                MaxTokens = 2000,
                MaxCharacters = 8000,
                MaxFileTokens = 1000,
                MaxFileCharacters = 4000,
                DependencyDepth = 2
            },
            RequiresRealProvider = true,
            Seed = 100,
            Parameters = new Dictionary<string, string>
            {
                ["contextWindowTokens"] = "4096"
            }
        };

    private static TaskExperimentResult Result(string variant, int repetition, decimal cost) =>
        new()
        {
            VariantId = variant,
            Repetition = repetition,
            Status = ExperimentResultStatus.Completed,
            Decision = TaskDecision.Verified,
            VerifiedCodeChange = true,
            FirstPassVerified = true,
            FilesChanged = 1,
            EstimatedCost = cost,
            InputTokens = variant == "naive" ? 1000 : 700,
            OutputTokens = 100,
            Duration = TimeSpan.FromSeconds(variant == "naive" ? 3 : 2),
            OriginalRepositoryUnchanged = true,
            EvidenceId = Guid.NewGuid(),
            EvidenceLocation = $"evidence/{variant}-{repetition}.json"
        };

    private static ExperimentPairedComparison Pair(
        int repetition,
        decimal referenceCost,
        decimal candidateCost) => new()
        {
            TaskId = "TASK-AB",
            Repetition = repetition,
            ReferenceVariantId = "naive",
            CandidateVariantId = "compiled",
            ReferenceStatus = ExperimentResultStatus.Completed,
            CandidateStatus = ExperimentResultStatus.Completed,
            BothCompleted = true,
            ReferenceVerifiedCodeChange = true,
            CandidateVerifiedCodeChange = true,
            ReferenceFirstPass = true,
            CandidateFirstPass = true,
            ReferenceTotalTokens = 1100,
            CandidateTotalTokens = 800,
            TotalTokenDelta = -300,
            ReferenceEstimatedCost = referenceCost,
            CandidateEstimatedCost = candidateCost,
            CostDelta = candidateCost - referenceCost,
            DurationDeltaSeconds = -1
        };

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null &&
            !File.Exists(Path.Combine(directory.FullName, "AECS.slnx")))
        {
            directory = directory.Parent;
        }
        return directory?.FullName ?? throw new DirectoryNotFoundException(
            "Unable to find the AECS repository root from the test output directory.");
    }
}
