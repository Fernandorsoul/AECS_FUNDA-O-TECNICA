using AECS.Application.Experiments;
using AECS.Domain.Enums;

namespace AECS.Application.Experiments;

public static class ExperimentReportFormatter
{
    public static string Format(ExperimentReport report)
    {
        var sb = new System.Text.StringBuilder();

        sb.AppendLine("AECS EXPERIMENT REPORT");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(report.DatasetId))
        {
            sb.AppendLine(
                $"Dataset: {report.DatasetId} v{report.DatasetVersion} ({report.DatasetHash})");
            sb.AppendLine($"Baseline: {report.BaselineCommit}");
            sb.AppendLine($"Reference variant: {report.ReferenceVariantId}");
        }
        sb.AppendLine($"Runs: {report.TotalTasks}");
        sb.AppendLine(
            $"Completed: {report.CompletedCount}, failed: {report.FailedCount}, " +
            $"skipped: {report.SkippedCount}");
        sb.AppendLine($"Verified: {report.VerifiedCount}");
        sb.AppendLine($"Rejected: {report.RejectedCount}");

        if (report.HumanReviewCount > 0)
            sb.AppendLine($"Human review: {report.HumanReviewCount}");

        sb.AppendLine();

        // Task details
        foreach (var result in report.Results)
        {
            var costRecord = report.CostRecords.SingleOrDefault(record =>
                record.RunKey == result.RunKey);
            var cost = costRecord?.EffectiveCostUsd is { } effectiveCost
                ? $"${effectiveCost:F4} ({costRecord.CostBasis})"
                : "cost unavailable";
            var decision = result.Status == ExperimentResultStatus.Completed
                ? result.Decision switch
                {
                    TaskDecision.Verified => "VERIFIED",
                    TaskDecision.Rejected => "REJECTED",
                    TaskDecision.HumanReviewRequired => "HUMAN_REVIEW",
                    _ => "UNKNOWN"
                }
                : result.Status.ToString().ToUpperInvariant();
            var runIdentity = string.IsNullOrEmpty(result.VariantId)
                ? result.TaskId
                : $"{result.TaskId}/{result.VariantId}/r{result.Repetition}";
            var rejectionReason = result.Status == ExperimentResultStatus.Failed
                ? $"  ({result.Failure})"
                : string.Empty;
            if (result.Status == ExperimentResultStatus.Completed &&
                result.Decision == TaskDecision.Rejected)
            {
                var failedVerifiers = result.Verifications
                    .Where(v => v.Value == VerificationStatus.Fail)
                    .Select(v => v.Key);
                rejectionReason = $"  ({string.Join(", ", failedVerifiers)})";
            }

            var objective = result.Objective.Length > 35
                ? result.Objective[..32] + "..."
                : result.Objective;

            sb.AppendLine($"{runIdentity,-24} {objective,-35} {result.Risk,-4} " +
                $"{decision,-12} {result.Duration.TotalSeconds,5:F1}s  " +
                $"{cost}  {result.FilesChanged} file(s), " +
                $"{result.RetryCount} retry(ies){rejectionReason}");
        }

        sb.AppendLine();

        var attemptedTasks = report.Results
            .Where(result => result.AgentAttempts.Count > 0)
            .ToList();
        if (attemptedTasks.Count > 0)
        {
            sb.AppendLine("AGENT ATTEMPTS");
            foreach (var result in attemptedTasks)
            {
                foreach (var attempt in result.AgentAttempts)
                {
                    sb.AppendLine(
                        $"{result.TaskId,-10} #{attempt.AttemptNumber,-3} {attempt.FailureKind,-14} " +
                        $"retry={attempt.WillRetry,-5} {attempt.DecisionReason}");
                }
            }
            sb.AppendLine();
        }

        if (report.PairedComparisons.Count > 0)
        {
            sb.AppendLine("PAIRED COMPARISONS");
            foreach (var pair in report.PairedComparisons)
            {
                var outcome = pair.BothCompleted
                    ? $"decisionChanged={pair.DecisionChanged} " +
                      $"vccDelta={pair.VerifiedCodeChangeDelta} " +
                      $"tokenDelta={pair.TotalTokenDelta} " +
                      $"durationDelta={pair.DurationDeltaSeconds:F2}s " +
                      $"estimatedCostDelta=${pair.CostDelta:F4}"
                    : pair.Failure;
                sb.AppendLine(
                    $"{pair.TaskId}/r{pair.Repetition} {pair.ReferenceVariantId} -> " +
                    $"{pair.CandidateVariantId}: {outcome}");
            }
            sb.AppendLine();
        }

        if (report.CostEfficiency.Aggregates.Count > 0)
        {
            sb.AppendLine("COST EFFICIENCY (RECONCILED WHEN AVAILABLE)");
            foreach (var aggregate in report.CostEfficiency.Aggregates)
            {
                var total = Money(aggregate.TotalEffectiveCostUsd);
                var cpvc = Money(aggregate.CpvcUsd);
                var interval = aggregate.CpvcConfidenceIntervalLower is { } lower &&
                    aggregate.CpvcConfidenceIntervalUpper is { } upper
                        ? $"[${lower:F4}, ${upper:F4}]"
                        : "unavailable";
                sb.AppendLine(
                    $"{aggregate.Dimension}/{aggregate.Value}: n={aggregate.SampleSize}, " +
                    $"VCC={aggregate.VerifiedCodeChanges}, cost={total}, CPVC={cpvc}, " +
                    $"95% CI={interval}, missingCost={aggregate.MissingCostCount}");
            }
            sb.AppendLine();
        }

        if (report.Analysis is not null)
        {
            sb.AppendLine($"HYPOTHESIS {report.Analysis.HypothesisId}");
            sb.AppendLine($"Primary metric: {report.Analysis.PrimaryMetric}");
            foreach (var variant in report.Analysis.Variants)
            {
                var yield = variant.VerifiedChangesPerEstimatedDollar is null
                    ? "unavailable"
                    : variant.VerifiedChangesPerEstimatedDollar.Value.ToString("F4");
                var tokensMedian = variant.TotalTokens.Median?.ToString("F1") ?? "unavailable";
                var latencyMedian = variant.LatencySeconds.Median is null
                    ? "unavailable"
                    : $"{variant.LatencySeconds.Median:F2}s";
                sb.AppendLine(
                    $"{variant.VariantId}: VCC={variant.VerifiedCodeChanges}/" +
                    $"{variant.ObservedRuns}, firstPass={variant.FirstPassRate:P1}, " +
                    $"tokensMedian={tokensMedian}, latencyMedian={latencyMedian}, " +
                    $"scope={variant.ScopeViolations}, rework={variant.ReworkAttempts}, " +
                    $"VCC/$estimated={yield}");
            }
            sb.AppendLine(
                $"Conclusion: {report.Analysis.Conclusion} — " +
                report.Analysis.ConclusionReason);
            sb.AppendLine();
        }

        var acceptanceCriteria = report.Results
            .Where(result => result.AcceptanceCriteria.Count > 0)
            .ToList();
        if (acceptanceCriteria.Count > 0)
        {
            sb.AppendLine("ACCEPTANCE EVIDENCE MATRIX");
            foreach (var result in acceptanceCriteria)
            {
                foreach (var criterion in result.AcceptanceCriteria)
                {
                    var reference = string.IsNullOrWhiteSpace(criterion.EvidenceReference)
                        ? "missing"
                        : $"{criterion.EvidenceType}:{criterion.EvidenceReference}";
                    sb.AppendLine(
                        $"{result.TaskId,-10} {criterion.CriterionId,-8} {criterion.Status,-5} " +
                        $"{reference} -> {criterion.Description}");
                }
            }
            sb.AppendLine();
        }

        // Summary
        sb.AppendLine("SUMMARY");
        sb.AppendLine($"Total duration: {report.TotalDuration.TotalSeconds:F1}s");
        sb.AppendLine($"Adapter estimate (not billed): ${report.TotalEstimatedCost:F4}");
        sb.AppendLine($"Effective cost: {Money(report.TotalCost)}");
        sb.AppendLine(
            $"First-pass verification rate: {report.FirstPassRate:F0}% " +
            $"({report.FirstPassVerifiedCount}/{report.CompletedCount})");
        sb.AppendLine($"Average files changed: {report.AverageFilesChanged:F1}");
        sb.AppendLine($"CPVC (Cost per Verified Code Change): {Money(report.Cpvc)}");

        return sb.ToString();
    }

    private static string Money(decimal? value) => value is null
        ? "unavailable"
        : $"${value.Value:F4}";
}
