using System.Text.Json;
using System.Text.Json.Nodes;
using AECS.Application;
using AECS.Application.ContextCompiler;
using AECS.Application.Experiments;
using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class ExperimentDatasetTests
{
    private const string Baseline = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    [Fact]
    public void Loader_ResolvesVersionedManifestAndRejectsDuplicateProperties()
    {
        using var fixture = ExperimentFixture.Create();
        var manifest = fixture.Manifest();
        var path = fixture.WriteManifest(manifest);

        var loaded = ExperimentDatasetLoader.Load(path);

        loaded.RepositoryPath.Should().Be(fixture.RepositoryPath);
        loaded.Manifest.SchemaVersion.Should().Be(ExperimentDatasetSchema.Version);
        loaded.ContractPath(loaded.Manifest.Tasks.Single()).Should().Be(fixture.TaskPath);

        var json = File.ReadAllText(path).Replace(
            "\"id\": \"dataset-1\"",
            "\"id\": \"forged\", \"id\": \"dataset-1\"",
            StringComparison.Ordinal);
        File.WriteAllText(path, json);

        var reload = () => ExperimentDatasetLoader.Load(path);
        reload.Should().Throw<InvalidOperationException>().WithMessage("*Duplicate dataset property*");
    }

    [Fact]
    public async Task FixedModelController_UsesTheVariantModelInsteadOfRiskRouting()
    {
        var plan = await new FixedModelExecutionController("experiment-model-v2").PlanAsync(
            new TaskContract
            {
                Id = "TASK-MODEL",
                Constraints = new TaskConstraints { SecurityRisk = RiskLevel.R4 }
            },
            CancellationToken.None);

        plan.Model.Should().Be("experiment-model-v2");
        plan.Risk.Should().Be(RiskLevel.R4);
    }

    [Fact]
    public async Task Runner_RepeatsPairsPersistsFailuresAndResumesWithoutDuplicateExecution()
    {
        using var fixture = ExperimentFixture.Create();
        var manifest = fixture.Manifest(
            repetitions: 2,
            variants:
            [
                Variant("control", ExperimentProvider.Mock, "mock-control"),
                Variant(
                    "candidate",
                    ExperimentProvider.Cloud,
                    "cloud-candidate",
                    seed: 100,
                    parameters: new Dictionary<string, string>
                    {
                        ["temperature"] = "0"
                    })
            ]);
        var loaded = fixture.Loaded(manifest);
        var artifacts = new ExperimentArtifactStore(fixture.OutputPath);
        var executions = new List<ExperimentRunDefinition>();
        var runner = new ExperimentRunner((definition, _) =>
        {
            executions.Add(definition);
            if (definition.Variant.Id == "candidate" && definition.Repetition == 2)
                throw new InvalidOperationException("provider failed on repetition two");
            return Task.FromResult(Execution(definition));
        });

        var first = await runner.RunDatasetAsync(
            loaded,
            Baseline,
            artifacts,
            resume: false,
            includeRealProviders: true,
            CancellationToken.None);

        executions.Should().HaveCount(4);
        executions.Where(run => run.Variant.Id == "candidate")
            .Select(run => run.EffectiveSeed).Should().Equal(100, 101);
        first.Results.Should().HaveCount(4);
        first.Results.Should().ContainSingle(result =>
            result.Status == ExperimentResultStatus.Failed &&
            result.Failure.Contains("provider failed", StringComparison.Ordinal));
        first.PairedComparisons.Should().HaveCount(2);
        first.PairedComparisons.Should().ContainSingle(pair => !pair.BothCompleted);
        first.Results.Where(result => result.Status == ExperimentResultStatus.Completed)
            .Should().OnlyContain(result =>
                result.EvidenceId != Guid.Empty &&
                !string.IsNullOrWhiteSpace(result.EvidenceLocation));
        File.Exists(artifacts.ReportPath).Should().BeTrue();
        File.Exists(artifacts.AnalysisCsvPath).Should().BeTrue();
        File.ReadAllLines(artifacts.ResultsCsvPath).Should().HaveCount(5);
        File.ReadAllLines(artifacts.ComparisonsCsvPath).Should().HaveCount(3);

        var resumedExecutions = 0;
        var resumedRunner = new ExperimentRunner((_, _) =>
        {
            resumedExecutions++;
            throw new InvalidOperationException("checkpoint was not reused");
        });
        var resumed = await resumedRunner.RunDatasetAsync(
            loaded,
            Baseline,
            artifacts,
            resume: true,
            includeRealProviders: true,
            CancellationToken.None);

        resumedExecutions.Should().Be(0);
        resumed.Results.Select(result => result.RunKey).Should().OnlyHaveUniqueItems();
        resumed.Results.Select(result => result.RunKey).Should()
            .BeEquivalentTo(first.Results.Select(result => result.RunKey));

        var checkpointPath = Directory.GetFiles(
            Path.Combine(fixture.OutputPath, "runs"),
            "*.json").First();
        var checkpoint = JsonNode.Parse(File.ReadAllText(checkpointPath))!.AsObject();
        checkpoint["result"]!["failure"] = "forged after completion";
        File.WriteAllText(checkpointPath, checkpoint.ToJsonString());
        var tamperedResume = () => resumedRunner.RunDatasetAsync(
            loaded,
            Baseline,
            artifacts,
            resume: true,
            includeRealProviders: true,
            CancellationToken.None);
        await tamperedResume.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*checkpoint is inconsistent*");
    }

    [Fact]
    public async Task Runner_SkipsRealProviderButKeepsItInPairedReport()
    {
        using var fixture = ExperimentFixture.Create();
        var manifest = fixture.Manifest(variants:
        [
            Variant("control", ExperimentProvider.Mock, "mock-control"),
            Variant("real", ExperimentProvider.Local, "qwen", seed: 7)
        ]);
        var executeCount = 0;
        var runner = new ExperimentRunner((definition, _) =>
        {
            executeCount++;
            return Task.FromResult(Execution(definition));
        });

        var report = await runner.RunDatasetAsync(
            fixture.Loaded(manifest),
            Baseline,
            new ExperimentArtifactStore(fixture.OutputPath),
            resume: false,
            includeRealProviders: false,
            CancellationToken.None);

        executeCount.Should().Be(1);
        report.Results.Should().ContainSingle(result =>
            result.VariantId == "real" && result.Status == ExperimentResultStatus.Skipped);
        report.PairedComparisons.Should().ContainSingle().Which.BothCompleted.Should().BeFalse();
    }

    [Fact]
    public async Task Runner_ResumesAfterCancellationFromTheFirstMissingRun()
    {
        using var fixture = ExperimentFixture.Create();
        var manifest = fixture.Manifest(
            repetitions: 2,
            variants:
            [
                Variant("control", ExperimentProvider.Mock, "mock-control"),
                Variant("candidate", ExperimentProvider.Mock, "mock-candidate")
            ]);
        var loaded = fixture.Loaded(manifest);
        var artifacts = new ExperimentArtifactStore(fixture.OutputPath);
        using var cancellation = new CancellationTokenSource();
        var firstAttempts = 0;
        var interruptedRunner = new ExperimentRunner((definition, _) =>
        {
            firstAttempts++;
            if (firstAttempts == 2)
            {
                cancellation.Cancel();
                throw new OperationCanceledException(cancellation.Token);
            }
            return Task.FromResult(Execution(definition));
        });

        var interrupted = () => interruptedRunner.RunDatasetAsync(
            loaded,
            Baseline,
            artifacts,
            resume: false,
            includeRealProviders: false,
            cancellation.Token);

        await interrupted.Should().ThrowAsync<OperationCanceledException>();
        Directory.GetFiles(Path.Combine(fixture.OutputPath, "runs"), "*.json")
            .Should().ContainSingle();

        var resumedAttempts = 0;
        var resumedRunner = new ExperimentRunner((definition, _) =>
        {
            resumedAttempts++;
            return Task.FromResult(Execution(definition));
        });
        var report = await resumedRunner.RunDatasetAsync(
            loaded,
            Baseline,
            artifacts,
            resume: true,
            includeRealProviders: false,
            CancellationToken.None);

        resumedAttempts.Should().Be(3);
        report.Results.Should().HaveCount(4);
        report.Results.Select(result => result.RunKey).Should().OnlyHaveUniqueItems();
    }

    [Fact]
    public async Task Runner_RejectsOutputInsideRepositoryBeforeExecuting()
    {
        using var fixture = ExperimentFixture.Create();
        var calls = 0;
        var runner = new ExperimentRunner((definition, _) =>
        {
            calls++;
            return Task.FromResult(Execution(definition));
        });

        var run = () => runner.RunDatasetAsync(
            fixture.Loaded(fixture.Manifest()),
            Baseline,
            new ExperimentArtifactStore(Path.Combine(fixture.RepositoryPath, "results")),
            resume: false,
            includeRealProviders: false,
            CancellationToken.None);

        await run.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("*outside the dataset repository*");
        calls.Should().Be(0);
    }

    private static ExperimentVariantDefinition Variant(
        string id,
        ExperimentProvider provider,
        string model,
        int? seed = null,
        Dictionary<string, string>? parameters = null) => new()
        {
            Id = id,
            Provider = provider,
            Model = model,
            ContextStrategy = id + "-context",
            Context = new ContextCompilationOptions
            {
                MaxTokens = 1000,
                MaxCharacters = 4000,
                MaxFileTokens = 500,
                MaxFileCharacters = 2000,
                DependencyDepth = id == "control" ? 2 : 1
            },
            RequiresRealProvider = provider != ExperimentProvider.Mock,
            Seed = seed,
            Parameters = parameters ?? new Dictionary<string, string>()
        };

    private static StagedExecutionResult Execution(ExperimentRunDefinition definition) => new()
    {
        Contract = new TaskContract
        {
            Id = definition.Task.Id,
            Objective = "Exercise experiment harness"
        },
        Risk = RiskLevel.R1,
        Model = definition.Variant.Model,
        Baseline = new BaselineSnapshot { Commit = definition.BaselineCommit },
        ContextManifest = new ContextManifest
        {
            StrategyVersion = ContextManifestSchema.StrategyVersion,
            Strategy = definition.Variant.ContextStrategy + "+fixture",
            ManifestHash = $"sha256:{new string('b', 64)}"
        },
        CandidateChangeSet = new CandidateChangeSet { ModifiedFiles = ["result.txt"] },
        Decision = new DecisionResult
        {
            Decision = TaskDecision.Verified,
            Reason = "fixture completed"
        },
        BudgetUsage = new ExecutionBudgetEvidence
        {
            WallClockElapsed = TimeSpan.FromSeconds(
                definition.Variant.Id == "control" ? 1 : 2)
        },
        EvidenceId = Guid.NewGuid(),
        EvidenceLocation = $"evidence/{definition.RunKey}.json",
        OriginalRepositoryUnchanged = true
    };

    private sealed class ExperimentFixture : IDisposable
    {
        private const string Prefix = "aecs-experiment-unit-tests-";

        private ExperimentFixture(string rootPath)
        {
            RootPath = rootPath;
            RepositoryPath = Path.Combine(rootPath, "repository");
            TaskPath = Path.Combine(RepositoryPath, "task.yaml");
            OutputPath = Path.Combine(rootPath, "output");
            Directory.CreateDirectory(RepositoryPath);
            File.WriteAllText(TaskPath, """
                task:
                  id: TASK-EXP
                  objective: Exercise experiment harness
                  scope:
                    allowed: [result.txt]
                    forbidden: []
                  verification:
                    build: disabled
                    unit_tests: disabled
                    scope: required
                """);
        }

        public string RootPath { get; }
        public string RepositoryPath { get; }
        public string TaskPath { get; }
        public string OutputPath { get; }

        public static ExperimentFixture Create()
        {
            var root = Path.Combine(Path.GetTempPath(), Prefix + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(root);
            return new ExperimentFixture(root);
        }

        public ExperimentDatasetManifest Manifest(
            int repetitions = 1,
            List<ExperimentVariantDefinition>? variants = null) => new()
            {
                Id = "dataset-1",
                Version = "1.0.0",
                Repository = new ExperimentRepositoryDefinition
                {
                    Path = "repository",
                    Baseline = Baseline
                },
                Repetitions = repetitions,
                ReferenceVariantId = "control",
                Tasks =
                [
                    new ExperimentTaskDefinition
                    {
                        Id = "TASK-EXP",
                        ContractPath = "task.yaml",
                        ExpectedDecision = TaskDecision.Verified
                    }
                ],
                Variants = variants ?? [Variant("control", ExperimentProvider.Mock, "mock")]
            };

        public string WriteManifest(ExperimentDatasetManifest manifest)
        {
            var path = Path.Combine(RootPath, "dataset.json");
            File.WriteAllText(
                path,
                JsonSerializer.Serialize(manifest, ExperimentDatasetLoader.SerializerOptions));
            return path;
        }

        public LoadedExperimentDataset Loaded(ExperimentDatasetManifest manifest) => new()
        {
            ManifestPath = WriteManifest(manifest),
            RepositoryPath = RepositoryPath,
            Manifest = manifest
        };

        public void Dispose()
        {
            var root = Path.GetFullPath(RootPath);
            var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
            if (!root.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(root).StartsWith(Prefix, StringComparison.Ordinal))
            {
                throw new InvalidOperationException("Refusing to remove unexpected fixture path.");
            }
            if (Directory.Exists(root))
                Directory.Delete(root, recursive: true);
        }
    }
}
