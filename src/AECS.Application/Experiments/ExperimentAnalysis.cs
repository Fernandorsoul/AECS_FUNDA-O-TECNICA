namespace AECS.Application.Experiments;

public sealed class ExperimentMetricDistribution
{
    public int Count { get; init; }
    public double? Minimum { get; init; }
    public double? FirstQuartile { get; init; }
    public double? Median { get; init; }
    public double? ThirdQuartile { get; init; }
    public double? Maximum { get; init; }
    public double? Mean { get; init; }
    public double? StandardDeviation { get; init; }
    public double? MeanConfidenceIntervalLower { get; init; }
    public double? MeanConfidenceIntervalUpper { get; init; }
}

public sealed class ExperimentVariantAnalysis
{
    public string VariantId { get; init; } = string.Empty;
    public string ContextStrategy { get; init; } = string.Empty;
    public int PlannedRuns { get; init; }
    public int ObservedRuns { get; init; }
    public int CompletedRuns { get; init; }
    public int FailedRuns { get; init; }
    public int SkippedRuns { get; init; }
    public int VerifiedCodeChanges { get; init; }
    public int FirstPassVerifiedChanges { get; init; }
    public double VerifiedCodeChangeRate { get; init; }
    public double FirstPassRate { get; init; }
    public double FailureRate { get; init; }
    public int ScopeViolations { get; init; }
    public double ScopeViolationRate { get; init; }
    public int ReworkAttempts { get; init; }
    public decimal TotalEstimatedCost { get; init; }
    public double? VerifiedChangesPerEstimatedDollar { get; init; }
    public ExperimentMetricDistribution TotalTokens { get; init; } = new();
    public ExperimentMetricDistribution EstimatedCost { get; init; } = new();
    public ExperimentMetricDistribution LatencySeconds { get; init; } = new();
    public ExperimentMetricDistribution ScopeViolationCount { get; init; } = new();
    public ExperimentMetricDistribution ReworkCount { get; init; } = new();
}

public sealed class ExperimentPairedAnalysis
{
    public string ReferenceVariantId { get; init; } = string.Empty;
    public string CandidateVariantId { get; init; } = string.Empty;
    public int PlannedPairs { get; init; }
    public int ObservedPairs { get; init; }
    public int CompletedPairs { get; init; }
    public int IncompletePairs { get; init; }
    public double? ReferenceVerifiedChangesPerEstimatedDollar { get; init; }
    public double? CandidateVerifiedChangesPerEstimatedDollar { get; init; }
    public double? RelativePrimaryMetricImprovement { get; init; }
    public ExperimentMetricDistribution VerifiedCodeChangeDelta { get; init; } = new();
    public ExperimentMetricDistribution FirstPassDelta { get; init; } = new();
    public ExperimentMetricDistribution TotalTokenDelta { get; init; } = new();
    public ExperimentMetricDistribution EstimatedCostDelta { get; init; } = new();
    public ExperimentMetricDistribution LatencyDeltaSeconds { get; init; } = new();
    public ExperimentMetricDistribution ScopeViolationDelta { get; init; } = new();
    public ExperimentMetricDistribution ReworkDelta { get; init; } = new();
    public ExperimentMetricDistribution VccPerEstimatedDollarDelta { get; init; } = new();
}

public static class ExperimentDecisionMetrics
{
    public const string RelativeVccPerEstimatedCostImprovement =
        "relative-vcc-per-estimated-cost-improvement";
    public const string PairedVccPerEstimatedCostDelta =
        "paired-vcc-per-estimated-cost-delta";
}

public sealed class ExperimentDecisionRuleAnalysis
{
    public ZeroReferencePolicy ZeroReferencePolicy { get; init; }
    public bool PolicyPreregistered { get; init; }
    public bool ZeroReferenceObserved { get; init; }
    public string EffectiveMetric { get; init; } = string.Empty;
    public double MinimumImprovement { get; init; }
}

public sealed class ExperimentHypothesisAnalysis
{
    public string HypothesisId { get; init; } = string.Empty;
    public string Hypothesis { get; init; } = string.Empty;
    public string PrimaryMetric { get; init; } = string.Empty;
    public int PreregisteredMinimumPairedSamples { get; init; }
    public double ConfidenceLevel { get; init; }
    public HypothesisConclusion Conclusion { get; init; }
    public string ConclusionReason { get; init; } = string.Empty;
    public ExperimentDecisionRuleAnalysis DecisionRule { get; init; } = new();
    public List<ExperimentVariantAnalysis> Variants { get; init; } = [];
    public ExperimentPairedAnalysis Pairs { get; init; } = new();
}

public static class ExperimentAnalyzer
{
    public static ExperimentHypothesisAnalysis? Analyze(
        ExperimentDatasetManifest manifest,
        IReadOnlyCollection<TaskExperimentResult> results,
        IReadOnlyCollection<ExperimentPairedComparison> pairs)
    {
        var protocol = manifest.Protocol;
        if (protocol is null)
            return null;

        var variants = manifest.Variants
            .Select(variant => AnalyzeVariant(
                variant,
                manifest.Tasks.Count * manifest.Repetitions,
                results.Where(result => result.VariantId == variant.Id).ToList()))
            .ToList();
        var reference = variants.Single(variant => variant.VariantId == manifest.ReferenceVariantId);
        var candidate = variants.Single(variant => variant.VariantId != manifest.ReferenceVariantId);
        var paired = AnalyzePairs(
            reference,
            candidate,
            manifest.Tasks.Count * manifest.Repetitions,
            pairs.Where(pair => pair.CandidateVariantId == candidate.VariantId).ToList());
        var decisionRule = DecisionRule(protocol, paired);
        var (conclusion, reason) = Conclude(protocol, candidate, paired, decisionRule);

        return new ExperimentHypothesisAnalysis
        {
            HypothesisId = protocol.HypothesisId,
            Hypothesis = protocol.Hypothesis,
            PrimaryMetric = protocol.PrimaryMetric,
            PreregisteredMinimumPairedSamples = protocol.MinimumPairedSamples,
            ConfidenceLevel = protocol.ConfidenceLevel,
            Conclusion = conclusion,
            ConclusionReason = reason,
            DecisionRule = decisionRule,
            Variants = variants,
            Pairs = paired
        };
    }

    private static ExperimentVariantAnalysis AnalyzeVariant(
        ExperimentVariantDefinition definition,
        int plannedRuns,
        IReadOnlyCollection<TaskExperimentResult> results)
    {
        var observed = results.Where(result => result.Status != ExperimentResultStatus.Skipped)
            .ToList();
        var completed = observed.Where(result => result.Status == ExperimentResultStatus.Completed)
            .ToList();
        var vcc = observed.Count(VerifiedCodeChangePolicy.IsVerified);
        var firstPass = observed.Count(result =>
            VerifiedCodeChangePolicy.IsVerified(result) && result.RetryCount == 0);
        var scopeViolations = observed.Sum(result => result.ScopeViolationCount);
        var totalCost = observed.Sum(result => result.EstimatedCost);

        return new ExperimentVariantAnalysis
        {
            VariantId = definition.Id,
            ContextStrategy = definition.ContextStrategy,
            PlannedRuns = plannedRuns,
            ObservedRuns = observed.Count,
            CompletedRuns = completed.Count,
            FailedRuns = results.Count(result => result.Status == ExperimentResultStatus.Failed),
            SkippedRuns = results.Count(result => result.Status == ExperimentResultStatus.Skipped),
            VerifiedCodeChanges = vcc,
            FirstPassVerifiedChanges = firstPass,
            VerifiedCodeChangeRate = Rate(vcc, observed.Count),
            FirstPassRate = Rate(firstPass, observed.Count),
            FailureRate = Rate(
                results.Count(result => result.Status == ExperimentResultStatus.Failed),
                observed.Count),
            ScopeViolations = scopeViolations,
            ScopeViolationRate = Rate(
                observed.Count(result => result.ScopeViolationCount > 0),
                observed.Count),
            ReworkAttempts = observed.Sum(result => result.ReworkCount),
            TotalEstimatedCost = totalCost,
            VerifiedChangesPerEstimatedDollar = totalCost > 0
                ? (double?)(vcc / (double)totalCost)
                : null,
            TotalTokens = Distribution(observed.Select(result => (double)result.TotalTokens)),
            EstimatedCost = Distribution(observed.Select(result => (double)result.EstimatedCost)),
            LatencySeconds = Distribution(observed.Select(result => result.Duration.TotalSeconds)),
            ScopeViolationCount = Distribution(
                observed.Select(result => (double)result.ScopeViolationCount)),
            ReworkCount = Distribution(observed.Select(result => (double)result.ReworkCount))
        };
    }

    private static ExperimentPairedAnalysis AnalyzePairs(
        ExperimentVariantAnalysis reference,
        ExperimentVariantAnalysis candidate,
        int plannedPairs,
        IReadOnlyCollection<ExperimentPairedComparison> pairs)
    {
        var observed = pairs.Where(pair =>
                pair.ReferenceStatus != ExperimentResultStatus.Skipped &&
                pair.CandidateStatus != ExperimentResultStatus.Skipped)
            .ToList();
        var primaryDeltas = observed
            .Where(pair => pair.ReferenceEstimatedCost > 0 && pair.CandidateEstimatedCost > 0)
            .Select(pair =>
                (pair.CandidateVerifiedCodeChange ? 1d : 0d) /
                (double)pair.CandidateEstimatedCost -
                (pair.ReferenceVerifiedCodeChange ? 1d : 0d) /
                (double)pair.ReferenceEstimatedCost)
            .ToList();
        double? relative = reference.VerifiedChangesPerEstimatedDollar is > 0 &&
            candidate.VerifiedChangesPerEstimatedDollar is not null
                ? candidate.VerifiedChangesPerEstimatedDollar.Value /
                    reference.VerifiedChangesPerEstimatedDollar.Value - 1
                : null;

        return new ExperimentPairedAnalysis
        {
            ReferenceVariantId = reference.VariantId,
            CandidateVariantId = candidate.VariantId,
            PlannedPairs = plannedPairs,
            ObservedPairs = observed.Count,
            CompletedPairs = observed.Count(pair => pair.BothCompleted),
            IncompletePairs = observed.Count(pair => !pair.BothCompleted),
            ReferenceVerifiedChangesPerEstimatedDollar =
                reference.VerifiedChangesPerEstimatedDollar,
            CandidateVerifiedChangesPerEstimatedDollar =
                candidate.VerifiedChangesPerEstimatedDollar,
            RelativePrimaryMetricImprovement = relative,
            VerifiedCodeChangeDelta = Distribution(
                observed.Select(pair => (double)pair.VerifiedCodeChangeDelta)),
            FirstPassDelta = Distribution(observed.Select(pair => (double)pair.FirstPassDelta)),
            TotalTokenDelta = Distribution(observed.Select(pair => (double)pair.TotalTokenDelta)),
            EstimatedCostDelta = Distribution(observed.Select(pair => (double)pair.CostDelta)),
            LatencyDeltaSeconds = Distribution(
                observed.Select(pair => pair.DurationDeltaSeconds)),
            ScopeViolationDelta = Distribution(
                observed.Select(pair => (double)pair.ScopeViolationDelta)),
            ReworkDelta = Distribution(observed.Select(pair => (double)pair.ReworkDelta)),
            VccPerEstimatedDollarDelta = Distribution(primaryDeltas)
        };
    }

    private static (HypothesisConclusion Conclusion, string Reason) Conclude(
        ExperimentProtocol protocol,
        ExperimentVariantAnalysis candidate,
        ExperimentPairedAnalysis pairs,
        ExperimentDecisionRuleAnalysis decisionRule)
    {
        if (pairs.ObservedPairs < protocol.MinimumPairedSamples)
        {
            return (HypothesisConclusion.Adjust,
                $"Only {pairs.ObservedPairs}/{protocol.MinimumPairedSamples} preregistered pairs " +
                "were observed; collect the missing runs without changing the protocol.");
        }
        if (candidate.FailureRate > protocol.DeathCriteria.MaximumCandidateFailureRate)
        {
            return (HypothesisConclusion.Abandon,
                $"Candidate failure rate {candidate.FailureRate:P2} exceeded the preregistered " +
                $"limit {protocol.DeathCriteria.MaximumCandidateFailureRate:P2}.");
        }
        if (candidate.ScopeViolationRate >
            protocol.DeathCriteria.MaximumCandidateScopeViolationRate)
        {
            return (HypothesisConclusion.Abandon,
                $"Candidate scope-violation rate {candidate.ScopeViolationRate:P2} exceeded the " +
                $"preregistered limit {protocol.DeathCriteria.MaximumCandidateScopeViolationRate:P2}.");
        }
        if (pairs.VccPerEstimatedDollarDelta.Count < protocol.MinimumPairedSamples)
        {
            return (HypothesisConclusion.Adjust,
                $"Only {pairs.VccPerEstimatedDollarDelta.Count}/" +
                $"{protocol.MinimumPairedSamples} pairs report positive estimated cost for " +
                "both variants; the preregistered cost-efficiency metric is unavailable.");
        }
        if (decisionRule.ZeroReferenceObserved)
        {
            if (decisionRule.ZeroReferencePolicy == ZeroReferencePolicy.Adjust)
            {
                return (HypothesisConclusion.Adjust,
                    "The reference produced zero verified code changes with valid cost, so " +
                    "relative VCC-per-estimated-cost improvement is undefined. A future " +
                    "dataset must preregister an absolute paired-delta policy to evaluate " +
                    "this case.");
            }

            var mean = pairs.VccPerEstimatedDollarDelta.Mean!.Value;
            var lower = pairs.VccPerEstimatedDollarDelta.MeanConfidenceIntervalLower!.Value;
            var threshold = decisionRule.MinimumImprovement;
            if (mean < threshold)
            {
                return (HypothesisConclusion.Abandon,
                    $"Absolute paired cost-efficiency improvement {mean:F4} did not reach " +
                    $"the preregistered minimum {threshold:F4}.");
            }
            if (lower > threshold)
            {
                return (HypothesisConclusion.Maintain,
                    "The absolute paired cost-efficiency improvement exceeded the " +
                    "preregistered minimum and the 95% mean confidence interval remained " +
                    "above that threshold.");
            }
            return (HypothesisConclusion.Adjust,
                "The absolute paired cost-efficiency point estimate reached the " +
                "preregistered minimum, but the 95% mean confidence interval did not remain " +
                "above that threshold; increase evidence without changing the protocol.");
        }
        if (pairs.RelativePrimaryMetricImprovement is null)
        {
            return (HypothesisConclusion.Adjust,
                "Relative VCC-per-estimated-cost improvement is unavailable despite paired " +
                "cost coverage; inspect the aggregate inputs before collecting more runs.");
        }
        if (pairs.RelativePrimaryMetricImprovement <
            protocol.DeathCriteria.MinimumRelativeImprovement)
        {
            return (HypothesisConclusion.Abandon,
                $"Relative primary-metric improvement {pairs.RelativePrimaryMetricImprovement:P2} " +
                $"did not reach the preregistered minimum " +
                $"{protocol.DeathCriteria.MinimumRelativeImprovement:P2}.");
        }
        if (pairs.VccPerEstimatedDollarDelta.MeanConfidenceIntervalLower > 0)
        {
            return (HypothesisConclusion.Maintain,
                "The candidate exceeded the preregistered effect and the 95% paired mean " +
                "confidence interval excludes zero.");
        }
        return (HypothesisConclusion.Adjust,
            "The observed effect is positive but the 95% paired mean confidence interval " +
            "still includes zero; increase evidence before changing the default.");
    }

    private static ExperimentDecisionRuleAnalysis DecisionRule(
        ExperimentProtocol protocol,
        ExperimentPairedAnalysis pairs)
    {
        var zeroReferenceObserved =
            pairs.ReferenceVerifiedChangesPerEstimatedDollar == 0;
        var zeroReferencePolicy = protocol.ZeroReferencePolicy ?? ZeroReferencePolicy.Adjust;
        var usesAbsoluteMetric = zeroReferenceObserved &&
            zeroReferencePolicy == ZeroReferencePolicy.AbsolutePairedDelta;
        return new ExperimentDecisionRuleAnalysis
        {
            ZeroReferencePolicy = zeroReferencePolicy,
            PolicyPreregistered = protocol.ZeroReferencePolicy is not null,
            ZeroReferenceObserved = zeroReferenceObserved,
            EffectiveMetric = usesAbsoluteMetric
                ? ExperimentDecisionMetrics.PairedVccPerEstimatedCostDelta
                : ExperimentDecisionMetrics.RelativeVccPerEstimatedCostImprovement,
            MinimumImprovement = usesAbsoluteMetric
                ? protocol.DeathCriteria.MinimumAbsoluteImprovement!.Value
                : protocol.DeathCriteria.MinimumRelativeImprovement
        };
    }

    public static ExperimentMetricDistribution Distribution(IEnumerable<double> values)
    {
        var ordered = values.OrderBy(value => value).ToArray();
        if (ordered.Length == 0)
            return new ExperimentMetricDistribution();
        var mean = ordered.Average();
        var standardDeviation = ordered.Length > 1
            ? Math.Sqrt(ordered.Sum(value => Math.Pow(value - mean, 2)) /
                (ordered.Length - 1))
            : 0;
        var margin = ordered.Length > 1
            ? Critical95(ordered.Length - 1) * standardDeviation / Math.Sqrt(ordered.Length)
            : 0;
        return new ExperimentMetricDistribution
        {
            Count = ordered.Length,
            Minimum = ordered[0],
            FirstQuartile = Quantile(ordered, 0.25),
            Median = Quantile(ordered, 0.5),
            ThirdQuartile = Quantile(ordered, 0.75),
            Maximum = ordered[^1],
            Mean = mean,
            StandardDeviation = standardDeviation,
            MeanConfidenceIntervalLower = mean - margin,
            MeanConfidenceIntervalUpper = mean + margin
        };
    }

    private static double Rate(int numerator, int denominator) =>
        denominator == 0 ? 0 : numerator / (double)denominator;

    private static double Quantile(IReadOnlyList<double> ordered, double probability)
    {
        var position = (ordered.Count - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        if (lower == upper)
            return ordered[lower];
        return ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }

    private static double Critical95(int degreesOfFreedom) => degreesOfFreedom switch
    {
        <= 1 => 12.706,
        2 => 4.303,
        3 => 3.182,
        4 => 2.776,
        5 => 2.571,
        6 => 2.447,
        7 => 2.365,
        8 => 2.306,
        9 => 2.262,
        10 => 2.228,
        <= 12 => 2.179,
        <= 15 => 2.131,
        <= 20 => 2.086,
        <= 25 => 2.060,
        <= 30 => 2.042,
        <= 40 => 2.021,
        <= 60 => 2.000,
        <= 120 => 1.980,
        _ => 1.960
    };
}
