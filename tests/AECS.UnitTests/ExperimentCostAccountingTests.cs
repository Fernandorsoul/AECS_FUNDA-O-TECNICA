using System.Text.Json;
using AECS.Application.Experiments;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using FluentAssertions;

namespace AECS.UnitTests;

public sealed class ExperimentCostAccountingTests
{
    private static readonly DateTime ExecutedAt =
        new(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void Reconciler_CombinesReconciledAndEstimatedCostsWithoutHidingMissingData()
    {
        var verified = Result('a', TaskDecision.Verified, Accounting(rateCard: 2m));
        var rejected = Result('b', TaskDecision.Rejected, Accounting(local: 1m));
        var failed = Result('c', null, accounting: null, ExperimentResultStatus.Failed);
        var skipped = Result('d', null, accounting: null, ExperimentResultStatus.Skipped);
        var results = new[] { verified, rejected, failed, skipped };

        var (unreconciled, _) = ExperimentCostReconciler.Build(results, null, ExecutedAt);
        var incomplete = ExperimentCostEfficiencyAnalyzer.Analyze(unreconciled)
            .Aggregates.Single(item => item.Dimension == "overall");

        incomplete.SampleSize.Should().Be(3);
        incomplete.SkippedCount.Should().Be(1);
        incomplete.RejectedCount.Should().Be(1);
        incomplete.FailedCount.Should().Be(1);
        incomplete.VerifiedCodeChanges.Should().Be(1);
        incomplete.MissingCostCount.Should().Be(1);
        incomplete.TotalEffectiveCostUsd.Should().BeNull();
        incomplete.CpvcUsd.Should().BeNull();

        var ledger = Ledger(new ExperimentCostReconciliationEntry
        {
            RunKey = failed.RunKey,
            EvidenceId = failed.EvidenceId,
            CostUsd = 0.5m,
            Reference = "provider-invoice/failed-run"
        });
        var (records, metadata) = ExperimentCostReconciler.Build(
            results,
            ledger,
            ExecutedAt);
        var analysis = ExperimentCostEfficiencyAnalyzer.Analyze(records);
        var overall = analysis.Aggregates.Single(item => item.Dimension == "overall");

        metadata.MatchedEntries.Should().Be(1);
        metadata.LedgerFingerprint.Should().MatchRegex("^sha256:[0-9a-f]{64}$");
        records.Single(record => record.RunKey == failed.RunKey).CostBasis.Should()
            .Be("reconciled");
        overall.KnownCostCount.Should().Be(3);
        overall.MissingCostCount.Should().Be(0);
        overall.TotalEffectiveCostUsd.Should().Be(3.5m);
        overall.CpvcUsd.Should().Be(3.5m);
        overall.CpvcConfidenceIntervalLower.Should().NotBeNull();
        overall.CpvcConfidenceIntervalUpper.Should().NotBeNull();
        overall.EffectiveCostDistribution.Count.Should().Be(3);
        overall.EffectiveCostDistribution.StandardDeviation.Should().BeGreaterThan(0);
        overall.MemberRunKeys.Should().BeEquivalentTo(results.Select(result => result.RunKey));
        overall.IncludedRunKeys.Should().HaveCount(3);
        overall.EvidenceIds.Should().HaveCount(3);
        analysis.Aggregates.Select(item => item.Dimension).Should().Contain(
            "model", "risk", "task", "strategy", "period");
    }

    [Fact]
    public void Analyzer_NoVcc_LeavesCpvcAndConfidenceIntervalUnavailable()
    {
        var rejected = Result('e', TaskDecision.Rejected, Accounting(rateCard: 1m));
        var (records, _) = ExperimentCostReconciler.Build(
            [rejected],
            null,
            ExecutedAt);

        var overall = ExperimentCostEfficiencyAnalyzer.Analyze(records)
            .Aggregates.Single(item => item.Dimension == "overall");

        overall.TotalEffectiveCostUsd.Should().Be(1m);
        overall.VerifiedCodeChanges.Should().Be(0);
        overall.CpvcUsd.Should().BeNull();
        overall.CpvcConfidenceIntervalLower.Should().BeNull();
        overall.CpvcConfidenceIntervalUpper.Should().BeNull();
    }

    [Fact]
    public void Reconciler_RejectsUnmatchedOrMismatchedEvidence()
    {
        var result = Result('f', TaskDecision.Verified, Accounting(rateCard: 1m));
        var unmatched = Ledger(new ExperimentCostReconciliationEntry
        {
            RunKey = new string('9', 64),
            CostUsd = 1m,
            Reference = "invoice/unmatched"
        });
        var mismatched = Ledger(new ExperimentCostReconciliationEntry
        {
            RunKey = result.RunKey,
            EvidenceId = Guid.NewGuid(),
            CostUsd = 1m,
            Reference = "invoice/mismatch"
        });

        var unmatchedAction = () => ExperimentCostReconciler.Build(
            [result], unmatched, ExecutedAt);
        var mismatchedAction = () => ExperimentCostReconciler.Build(
            [result], mismatched, ExecutedAt);

        unmatchedAction.Should().Throw<InvalidOperationException>()
            .WithMessage("*unmatched run*");
        mismatchedAction.Should().Throw<InvalidOperationException>()
            .WithMessage("*evidence does not match*");
    }

    [Fact]
    public void ReconciliationLoader_RejectsDuplicateProperties()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aecs-cost-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, $$"""
                {
                  "schemaVersion": "aecs.cost-reconciliation/v1",
                  "currency": "USD",
                  "source": "invoice-a",
                  "source": "invoice-b",
                  "capturedAtUtc": "2026-09-01T12:00:00Z",
                  "entries": []
                }
                """);

            var action = () => ExperimentCostReconciliationLoader.Load(path);

            action.Should().Throw<InvalidOperationException>()
                .WithMessage("*Duplicate cost reconciliation property*");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ReconciliationLoader_RejectsUnknownPropertiesInsteadOfIgnoringTypos()
    {
        var path = Path.Combine(Path.GetTempPath(), $"aecs-cost-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, """
                {
                  "schemaVersion": "aecs.cost-reconciliation/v1",
                  "currency": "USD",
                  "source": "invoice-a",
                  "capturedAtUtc": "2026-09-01T12:00:00Z",
                  "costInUsd": 1.0,
                  "entries": []
                }
                """);

            var action = () => ExperimentCostReconciliationLoader.Load(path);

            action.Should().Throw<JsonException>();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void VccPolicy_RequiresEvidenceScopeIntegrityAndUnchangedRepository()
    {
        var valid = Result('1', TaskDecision.Verified, Accounting(rateCard: 1m));
        var missingEvidence = Copy(valid, evidenceId: Guid.Empty);
        var noCandidateFiles = Copy(valid, filesChanged: 0);
        var scopeViolation = Copy(valid, scopeViolationCount: 1);
        var changedRepository = Copy(valid, unchanged: false);

        VerifiedCodeChangePolicy.IsVerified(valid).Should().BeTrue();
        VerifiedCodeChangePolicy.Reason(missingEvidence).Should().Be("missing-origin-evidence");
        VerifiedCodeChangePolicy.Reason(noCandidateFiles).Should().Be("no-candidate-files");
        VerifiedCodeChangePolicy.Reason(scopeViolation).Should().Be("scope-violation");
        VerifiedCodeChangePolicy.Reason(changedRepository).Should()
            .Be("original-repository-changed");
    }

    [Fact]
    public void OptionalAccounting_PreservesLegacySerializationShape()
    {
        var json = JsonSerializer.Serialize(
            new AgentRunResult(),
            ExperimentDatasetLoader.SerializerOptions);

        json.Should().NotContain("usageAccounting");
    }

    private static ExperimentCostReconciliationLedger Ledger(
        params ExperimentCostReconciliationEntry[] entries) => new()
        {
            Source = "provider-invoice-export",
            CapturedAtUtc = ExecutedAt,
            Entries = [.. entries]
        };

    private static AgentUsageAccounting Accounting(
        decimal? rateCard = null,
        decimal? local = null) => new()
        {
            Adapter = rateCard is not null ? "CloudAdapter" : "OllamaAdapter",
            Model = "model-a",
            EstimatedInputTokens = 100,
            ReservedOutputTokens = 20,
            ProviderInputTokens = 110,
            ProviderOutputTokens = 15,
            RateCardEstimatedCostUsd = rateCard,
            LocalResourceEstimatedCostUsd = local,
            CostComplete = true,
            PricingTableVersion = rateCard is null ? string.Empty : "2026-09-01",
            LocalCostPolicyVersion = local is null ? string.Empty : "aecs.local-compute-cost/v1"
        };

    private static TaskExperimentResult Result(
        char runKey,
        TaskDecision? decision,
        AgentUsageAccounting? accounting,
        ExperimentResultStatus status = ExperimentResultStatus.Completed) => new()
        {
            RunKey = new string(runKey, 64),
            Status = status,
            TaskId = "TASK-COST",
            Model = "model-a",
            RequestedModel = "model-a",
            Risk = RiskLevel.R2,
            ContextStrategy = "graph-ranked",
            ActualContextStrategy = "graph-ranked",
            Decision = decision,
            FilesChanged = status == ExperimentResultStatus.Skipped ? 0 : 1,
            OriginalRepositoryUnchanged = true,
            EvidenceId = status == ExperimentResultStatus.Skipped ? Guid.Empty : Guid.NewGuid(),
            EvidenceLocation = status == ExperimentResultStatus.Skipped
                ? string.Empty
                : $"evidence/{runKey}.json",
            FinishedAtUtc = ExecutedAt,
            UsageAccounting = accounting
        };

    private static TaskExperimentResult Copy(
        TaskExperimentResult source,
        Guid? evidenceId = null,
        int? filesChanged = null,
        int? scopeViolationCount = null,
        bool? unchanged = null) => new()
        {
            RunKey = source.RunKey,
            Status = source.Status,
            TaskId = source.TaskId,
            Model = source.Model,
            RequestedModel = source.RequestedModel,
            Risk = source.Risk,
            ContextStrategy = source.ContextStrategy,
            ActualContextStrategy = source.ActualContextStrategy,
            Decision = source.Decision,
            FilesChanged = filesChanged ?? source.FilesChanged,
            OriginalRepositoryUnchanged = unchanged ?? source.OriginalRepositoryUnchanged,
            EvidenceId = evidenceId ?? source.EvidenceId,
            EvidenceLocation = source.EvidenceLocation,
            FinishedAtUtc = source.FinishedAtUtc,
            UsageAccounting = source.UsageAccounting,
            ScopeViolationCount = scopeViolationCount ?? source.ScopeViolationCount
        };
}
