using System.Text.Json;
using AECS.Application;
using AECS.Application.ContextCompiler;
using AECS.Application.Experiments;
using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class ExperimentFactorialTests : IDisposable
{
    private const string Baseline = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private readonly string _root;
    private readonly string _repositoryPath;
    private readonly string _outputPath;

    public ExperimentFactorialTests()
    {
        _root = Path.Combine(
            Path.GetTempPath(),
            "aecs-factorial-tests-" + Guid.NewGuid().ToString("N"));
        _repositoryPath = Path.Combine(_root, "repository");
        _outputPath = Path.Combine(_root, "output");
        Directory.CreateDirectory(_repositoryPath);
        foreach (var taskId in new[] { "EXP-F001", "EXP-F002" })
        {
            File.WriteAllText(
                Path.Combine(_repositoryPath, taskId + ".yaml"),
                $"""
                 schema_version: aecs.task-contract/v1
                 task:
                   id: {taskId}
                   objective: Factorial protocol fixture task
                   scope:
                     allowed: [result.txt]
                     forbidden: []
                   verification:
                     build: disabled
                     unit_tests: disabled
                     scope: required
                 """);
        }
    }

    public void Dispose()
    {
        var resolved = Path.GetFullPath(_root);
        var temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar);
        if (resolved.StartsWith(temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) &&
            Directory.Exists(resolved))
        {
            Directory.Delete(resolved, recursive: true);
        }
    }

    private ExperimentDatasetManifest Manifest() => new()
    {
        SchemaVersion = ExperimentDatasetSchema.FactorialLedgerContextAbVersion,
        Id = "factorial-1",
        Version = "1.0.0",
        Repository = new ExperimentRepositoryDefinition
        {
            Path = "repository",
            Baseline = Baseline
        },
        Repetitions = 1,
        ReferenceVariantId = "A",
        Protocol = new ExperimentProtocol
        {
            Design = ExperimentDesigns.FactorialLedgerContext2x2,
            HypothesisId = "H1-LC",
            Hypothesis = "Constraint Ledger and graph-ranked context interact",
            PrimaryMetric = ExperimentDesigns.VccPerEstimatedCost,
            MinimumPairedSamples = 2,
            ConfidenceLevel = 0.95,
            DeathCriteria = new ExperimentDeathCriteria
            {
                MinimumRelativeImprovement = 0.0,
                MaximumCandidateFailureRate = 1.0,
                MaximumCandidateScopeViolationRate = 0.0
            }
        },
        Tasks =
        [
            new ExperimentTaskDefinition
            {
                Id = "EXP-F001",
                ContractPath = "EXP-F001.yaml",
                ExpectedDecision = TaskDecision.Verified
            },
            new ExperimentTaskDefinition
            {
                Id = "EXP-F002",
                ContractPath = "EXP-F002.yaml",
                ExpectedDecision = TaskDecision.Verified
            }
        ],
        Variants =
        [
            Arm("A", ContextStrategyIds.NaivePathOrder, constraintLedger: false),
            Arm("B", ContextStrategyIds.NaivePathOrder, constraintLedger: true),
            Arm("C", ContextStrategyIds.GraphRanked, constraintLedger: false),
            Arm("D", ContextStrategyIds.GraphRanked, constraintLedger: true)
        ]
    };

    private static ExperimentVariantDefinition Arm(
        string id,
        string contextStrategy,
        bool constraintLedger) => new()
    {
        Id = id,
        Provider = ExperimentProvider.Mock,
        Model = "mock-factorial-v1",
        ContextStrategy = contextStrategy,
        ConstraintLedgerEnabled = constraintLedger,
        Context = new ContextCompilationOptions
        {
            MaxTokens = 2000,
            MaxCharacters = 8000,
            MaxFileTokens = 1000,
            MaxFileCharacters = 4000,
            DependencyDepth = 2
        },
        RequiresRealProvider = false,
        Parameters = new Dictionary<string, string>()
    };

    private LoadedExperimentDataset Loaded(ExperimentDatasetManifest manifest)
    {
        var path = Path.Combine(_root, "dataset.json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(manifest, ExperimentDatasetLoader.SerializerOptions));
        return new LoadedExperimentDataset
        {
            ManifestPath = path,
            RepositoryPath = _repositoryPath,
            Manifest = manifest
        };
    }

    private static StagedExecutionResult Execution(ExperimentRunDefinition definition)
    {
        var ledgerEnabled = definition.Variant.ConstraintLedgerEnabled;
        var tokensByArm = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["A"] = 100,
            ["B"] = 120,
            ["C"] = 90,
            ["D"] = 140
        };
        var decision = definition.Variant.Id == "D" && definition.Task.Id == "EXP-F002"
            ? TaskDecision.Rejected
            : TaskDecision.Verified;
        var results = new List<VerificationResult>
        {
            new() { Verifier = "AgentSuccess", Status = VerificationStatus.Pass },
            new() { Verifier = "Application", Status = VerificationStatus.Pass },
            new() { Verifier = "NonEmptyChange", Status = VerificationStatus.Pass },
            new() { Verifier = "Scope", Status = VerificationStatus.Pass },
            new() { Verifier = "Budget", Status = VerificationStatus.Pass }
        };
        if (ledgerEnabled)
        {
            var violated = decision == TaskDecision.Rejected ? 0 : 0;
            var pending = decision == TaskDecision.Rejected ? 1 : 0;
            results.Add(new VerificationResult
            {
                Verifier = ConstraintLedgerVerifier.Name,
                Status = VerificationStatus.Pass,
                Severity = pending > 0 ? Severity.Warning : Severity.Info,
                ConstraintLedger = new ConstraintLedgerVerificationEvidence
                {
                    SetId = $"set-{definition.Variant.Id}-{definition.Task.Id}",
                    SetRevision = 1,
                    SetCanonicalSha256 = "sha256:" + new string('b', 64),
                    ActiveCount = 2,
                    Assessments =
                    [
                        new ConstraintAssessment
                        {
                            RequirementKey = "budget.limits",
                            Revision = 1,
                            Kind = ConstraintKind.BudgetLimit,
                            Verifiability = ConstraintVerifiability.Deterministic,
                            VerifierName = "Budget",
                            Outcome = ConstraintAssessmentOutcome.Satisfied
                        },
                        pending > 0
                            ? new ConstraintAssessment
                            {
                                RequirementKey = "human.style",
                                Revision = 1,
                                Kind = ConstraintKind.ProcessInvariant,
                                Verifiability = ConstraintVerifiability.Manual,
                                Outcome = ConstraintAssessmentOutcome.PendingReview
                            }
                            : new ConstraintAssessment
                            {
                                RequirementKey = "scope.allowed",
                                Revision = 1,
                                Kind = ConstraintKind.ScopeBoundary,
                                Verifiability = ConstraintVerifiability.Deterministic,
                                VerifierName = "Scope",
                                Outcome = ConstraintAssessmentOutcome.Satisfied
                            }
                    ]
                }
            });
        }

        return new StagedExecutionResult
        {
            Contract = new TaskContract
            {
                Id = definition.Task.Id,
                Objective = "Factorial protocol fixture task"
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
                Decision = decision,
                Reason = "fixture"
            },
            BudgetUsage = new ExecutionBudgetEvidence
            {
                WallClockElapsed = TimeSpan.FromSeconds(1)
            },
            AgentResult = new AgentRunResult
            {
                Success = decision == TaskDecision.Verified,
                InputTokens = tokensByArm[definition.Variant.Id] / 2,
                OutputTokens = tokensByArm[definition.Variant.Id] / 2,
                EstimatedCost = tokensByArm[definition.Variant.Id] * 0.0001m,
                ExitReason = "Completed"
            },
            VerificationResults = results,
            EvidenceId = Guid.NewGuid(),
            EvidenceLocation = $"evidence/{definition.RunKey}.json",
            OriginalRepositoryUnchanged = true
        };
    }

    [Fact]
    public void Loader_AcceptsValidatedFactorialProtocol()
    {
        var loaded = Loaded(Manifest());
        var act = () => ExperimentDatasetLoader.Load(loaded.ManifestPath);

        act.Should().NotThrow();
    }

    [Theory]
    [InlineData("A", true, "naive-path-order", false)]
    [InlineData("B", false, "naive-path-order", false)]
    [InlineData("A", false, "graph-ranked", false)]
    [InlineData("D", false, "graph-ranked", false)]
    public void Loader_RejectsWrongArmAssignment(
        string armId,
        bool ledger,
        string strategy,
        bool _)
    {
        var manifest = Manifest();
        var broken = new List<ExperimentVariantDefinition>();
        foreach (var variant in manifest.Variants)
        {
            broken.Add(variant.Id == armId
                ? Arm(armId, strategy, ledger)
                : variant);
        }

        var reassigned = new ExperimentDatasetManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            Id = manifest.Id,
            Version = manifest.Version,
            Repository = manifest.Repository,
            Repetitions = manifest.Repetitions,
            ReferenceVariantId = manifest.ReferenceVariantId,
            Protocol = manifest.Protocol,
            Tasks = manifest.Tasks,
            Variants = broken
        };

        var path = Path.Combine(_root, "broken-" + armId + ledger + strategy + ".json");
        File.WriteAllText(
            path,
            JsonSerializer.Serialize(reassigned, ExperimentDatasetLoader.SerializerOptions));
        var act = () => ExperimentDatasetLoader.Load(path);
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*unsupported schema*");
    }

    [Fact]
    public async Task Runner_FactorialDataset_ProducesArmSummariesAndInteractionContrast()
    {
        var manifest = Manifest();
        var observedLedgerFlags = new List<(string Variant, bool Ledger)>();
        var runner = new ExperimentRunner((definition, _) =>
        {
            observedLedgerFlags.Add((definition.Variant.Id, definition.Variant.ConstraintLedgerEnabled));
            return Task.FromResult(Execution(definition));
        });

        var report = await runner.RunDatasetAsync(
            Loaded(manifest),
            Baseline,
            new ExperimentArtifactStore(_outputPath),
            resume: false,
            includeRealProviders: false,
            CancellationToken.None);

        observedLedgerFlags.Should().HaveCount(8);
        observedLedgerFlags.Where(item => item.Variant == "A" || item.Variant == "C")
            .Should().OnlyContain(item => !item.Ledger);
        observedLedgerFlags.Where(item => item.Variant == "B" || item.Variant == "D")
            .Should().OnlyContain(item => item.Ledger);

        report.Factorial.Should().NotBeNull();
        report.Factorial!.Design.Should().Be(ExperimentDesigns.FactorialLedgerContext2x2);
        report.Factorial.ArmSummaries.Should().HaveCount(4);
        report.Factorial.ArmSummaries.Should().Contain(arm =>
            arm.ArmId == "A" && !arm.ConstraintLedgerEnabled &&
            arm.ConstraintRetentionRate == null);
        report.Factorial.ArmSummaries.Should().Contain(arm =>
            arm.ArmId == "B" && arm.ConstraintLedgerEnabled &&
            arm.ConstraintRetentionRate == 1.0 &&
            arm.ConstraintVerificationCoverage == 1.0);
        report.Factorial.Contrasts.Select(contrast => contrast.Name).Should().BeEquivalentTo(
        [
            "ledger-effect-baseline",
            "ledger-effect-graph-ranked",
            "harness-effect-ledger-off",
            "harness-effect-ledger-on",
            "interaction"
        ]);
        var ledgerBaseline = report.Factorial.Contrasts
            .Single(contrast => contrast.Name == "ledger-effect-baseline");
        ledgerBaseline.TotalTokenDelta.Should().Be(
            (120 * 2) - (100 * 2));
        var interaction = report.Factorial.Contrasts
            .Single(contrast => contrast.Name == "interaction");
        // (D-C)-(B-A) tokens: ((140-90)-(120-100)) * 2 tasks
        interaction.TotalTokenDelta.Should().Be((50 - 20) * 2);

        // D arm rejected EXP-F002 with pending-only ledger evidence → false-block candidate
        report.Factorial.FalseBlockCandidates.Should().Be(1);
        report.Results.Should().OnlyContain(result =>
            result.ConstraintLedgerEnabled ==
                (result.VariantId == "B" || result.VariantId == "D"));
        report.Analysis.Should().BeNull("legacy hypothesis analysis does not apply to factorial design");
    }

    [Fact]
    public async Task Runner_CapturedVariantLedgerFlag_MatchesManifestArms()
    {
        var manifest = Manifest();
        var runner = new ExperimentRunner((definition, _) =>
            Task.FromResult(Execution(definition)));

        var report = await runner.RunDatasetAsync(
            Loaded(manifest),
            Baseline,
            new ExperimentArtifactStore(Path.Combine(_root, "output-flags")),
            resume: false,
            includeRealProviders: false,
            CancellationToken.None);

        foreach (var result in report.Results)
        {
            var arm = manifest.Variants.Single(variant =>
                variant.Id == result.VariantId);
            result.ConstraintLedgerEnabled.Should().Be(arm.ConstraintLedgerEnabled);
        }
    }
}
