using AECS.Domain.Enums;

namespace AECS.Application.Experiments;

public sealed class ExperimentArmLedgerSummary
{
    public string ArmId { get; init; } = string.Empty;
    public string VariantId { get; init; } = string.Empty;
    public bool ConstraintLedgerEnabled { get; init; }
    public string ContextStrategy { get; init; } = string.Empty;
    public int CompletedCount { get; init; }
    public int VerifiedCount { get; init; }
    public int FirstPassCount { get; init; }
    public int ConstraintActiveTotal { get; init; }
    public int ConstraintSatisfiedTotal { get; init; }
    public int ConstraintViolatedTotal { get; init; }
    public int ConstraintPendingReviewTotal { get; init; }
    public int TotalTokens { get; init; }
    public decimal EstimatedCost { get; init; }

    /// <summary>Satisfied ÷ (satisfied + violated); null when the denominator is zero (N/D).</summary>
    public double? ConstraintRetentionRate { get; init; }

    /// <summary>(satisfied + violated) ÷ active; null when active is zero (N/D).</summary>
    public double? ConstraintVerificationCoverage { get; init; }
}

public sealed class ExperimentFactorialContrast
{
    public string Name { get; init; } = string.Empty;
    public string LeftArm { get; init; } = string.Empty;
    public string RightArm { get; init; } = string.Empty;
    public int PairedSampleCount { get; init; }
    public int BothCompletedCount { get; init; }
    public int VerifiedCodeChangeDelta { get; init; }
    public int FirstPassDelta { get; init; }
    public int TotalTokenDelta { get; init; }
    public decimal EstimatedCostDelta { get; init; }
}

/// <summary>
/// Protocol-only analysis for the pre-registered 2×2 factorial design
/// (plan §6): Ledger on/off × harness baseline/graph-ranked. Contrasts keep the
/// two factors separable — the interaction term is reported explicitly so a
/// joint improvement is never attributed to a single component.
/// </summary>
public sealed class ExperimentFactorialLedgerContextAnalysis
{
    public string Design { get; init; } = ExperimentDesigns.FactorialLedgerContext2x2;
    public List<string> Limitations { get; init; } = [];
    public List<ExperimentArmLedgerSummary> ArmSummaries { get; init; } = [];
    public List<ExperimentFactorialContrast> Contrasts { get; init; } = [];
    public int FalseBlockCandidates { get; init; }

    public static ExperimentFactorialLedgerContextAnalysis? Build(
        ExperimentDatasetManifest manifest,
        IReadOnlyList<TaskExperimentResult> results)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        ArgumentNullException.ThrowIfNull(results);
        if (manifest.SchemaVersion != ExperimentDatasetSchema.FactorialLedgerContextAbVersion)
        {
            return null;
        }

        var arms = new[] { "A", "B", "C", "D" };
        var byArm = manifest.Variants
            .Where(variant => arms.Contains(variant.Id, StringComparer.Ordinal))
            .ToDictionary(variant => variant.Id, StringComparer.Ordinal);

        var summaries = new List<ExperimentArmLedgerSummary>();
        foreach (var arm in arms)
        {
            if (!byArm.TryGetValue(arm, out var variant))
            {
                continue;
            }

            var runs = results.Where(result =>
                result.VariantId.Equals(variant.Id, StringComparison.Ordinal)).ToList();
            var completed = runs.Where(result =>
                result.Status == ExperimentResultStatus.Completed).ToList();
            var satisfied = runs.Sum(result => result.ConstraintSatisfiedCount);
            var violated = runs.Sum(result => result.ConstraintViolatedCount);
            var pending = runs.Sum(result => result.ConstraintPendingReviewCount);
            var active = runs.Sum(result => result.ConstraintActiveCount);
            summaries.Add(new ExperimentArmLedgerSummary
            {
                ArmId = arm,
                VariantId = variant.Id,
                ConstraintLedgerEnabled = variant.ConstraintLedgerEnabled,
                ContextStrategy = variant.ContextStrategy,
                CompletedCount = completed.Count,
                VerifiedCount = completed.Count(VerifiedCodeChangePolicy.IsVerified),
                FirstPassCount = completed.Count(result =>
                    VerifiedCodeChangePolicy.IsVerified(result) && result.RetryCount == 0),
                ConstraintActiveTotal = active,
                ConstraintSatisfiedTotal = satisfied,
                ConstraintViolatedTotal = violated,
                ConstraintPendingReviewTotal = pending,
                TotalTokens = runs.Sum(result => result.TotalTokens),
                EstimatedCost = runs.Sum(result => result.EstimatedCost),
                ConstraintRetentionRate = satisfied + violated == 0
                    ? null
                    : (double)satisfied / (satisfied + violated),
                ConstraintVerificationCoverage = active == 0
                    ? null
                    : (double)(satisfied + violated) / active
            });
        }

        var pairs = new List<(TaskExperimentResult? A, TaskExperimentResult? B,
            TaskExperimentResult? C, TaskExperimentResult? D)>();
        foreach (var group in results.GroupBy(result =>
                     $"{result.TaskId}\n{result.Repetition}", StringComparer.Ordinal))
        {
            TaskExperimentResult? Find(string arm) => group.SingleOrDefault(result =>
                result.VariantId.Equals(arm, StringComparison.Ordinal));
            var tuple = (Find("A"), Find("B"), Find("C"), Find("D"));
            if (tuple is { Item1: not null, Item2: not null, Item3: not null, Item4: not null })
            {
                pairs.Add(tuple);
            }
        }

        var contrasts = new List<ExperimentFactorialContrast>
        {
            Contrast("ledger-effect-baseline", "A", "B", pairs,
                pair => pair.A!, pair => pair.B!),
            Contrast("ledger-effect-graph-ranked", "C", "D", pairs,
                pair => pair.C!, pair => pair.D!),
            Contrast("harness-effect-ledger-off", "A", "C", pairs,
                pair => pair.A!, pair => pair.C!),
            Contrast("harness-effect-ledger-on", "B", "D", pairs,
                pair => pair.B!, pair => pair.D!)
        };
        contrasts.Add(InteractionContrast(pairs));

        var falseBlocks = pairs.Count(pair =>
            VerifiedCodeChangePolicy.IsVerified(pair.A!) &&
            pair.B!.Status == ExperimentResultStatus.Completed &&
            pair.B.Decision == TaskDecision.Rejected &&
            pair.B.ConstraintViolatedCount == 0) +
            pairs.Count(pair =>
                VerifiedCodeChangePolicy.IsVerified(pair.C!) &&
                pair.D!.Status == ExperimentResultStatus.Completed &&
                pair.D.Decision == TaskDecision.Rejected &&
                pair.D.ConstraintViolatedCount == 0);

        return new ExperimentFactorialLedgerContextAnalysis
        {
            ArmSummaries = summaries,
            Contrasts = contrasts,
            FalseBlockCandidates = falseBlocks,
            Limitations =
            [
                "Protocol validation only: results depend on the configured provider; " +
                "a mock run does not demonstrate efficacy gains.",
                "CRR is reported together with verification coverage; pending/manual " +
                "assessments are never counted as satisfied.",
                "Denominator-zero metrics are reported as null (N/D), never as 0 or 100%.",
                "Interaction contrast must be inspected before attributing a joint " +
                "improvement to Ledger or harness alone."
            ]
        };
    }

    private static ExperimentFactorialContrast Contrast(
        string name,
        string leftArm,
        string rightArm,
        IReadOnlyList<(TaskExperimentResult? A, TaskExperimentResult? B,
            TaskExperimentResult? C, TaskExperimentResult? D)> pairs,
        Func<(TaskExperimentResult? A, TaskExperimentResult? B,
            TaskExperimentResult? C, TaskExperimentResult? D), TaskExperimentResult> left,
        Func<(TaskExperimentResult? A, TaskExperimentResult? B,
            TaskExperimentResult? C, TaskExperimentResult? D), TaskExperimentResult> right)
    {
        var verified = 0;
        var firstPass = 0;
        var tokens = 0;
        var cost = 0m;
        var bothCompleted = 0;
        foreach (var pair in pairs)
        {
            var l = left(pair);
            var r = right(pair);
            var both = l.Status == ExperimentResultStatus.Completed &&
                r.Status == ExperimentResultStatus.Completed;
            if (both)
            {
                bothCompleted++;
            }

            verified += Bool(VerifiedCodeChangePolicy.IsVerified(r)) -
                Bool(VerifiedCodeChangePolicy.IsVerified(l));
            firstPass += Bool(VerifiedCodeChangePolicy.IsVerified(r) && r.RetryCount == 0) -
                Bool(VerifiedCodeChangePolicy.IsVerified(l) && l.RetryCount == 0);
            tokens += r.TotalTokens - l.TotalTokens;
            cost += r.EstimatedCost - l.EstimatedCost;
        }

        return new ExperimentFactorialContrast
        {
            Name = name,
            LeftArm = leftArm,
            RightArm = rightArm,
            PairedSampleCount = pairs.Count,
            BothCompletedCount = bothCompleted,
            VerifiedCodeChangeDelta = verified,
            FirstPassDelta = firstPass,
            TotalTokenDelta = tokens,
            EstimatedCostDelta = cost
        };
    }

    private static ExperimentFactorialContrast InteractionContrast(
        IReadOnlyList<(TaskExperimentResult? A, TaskExperimentResult? B,
            TaskExperimentResult? C, TaskExperimentResult? D)> pairs)
    {
        var verified = 0;
        var firstPass = 0;
        var tokens = 0;
        var cost = 0m;
        var bothCompleted = 0;
        foreach (var pair in pairs)
        {
            var a = pair.A!;
            var b = pair.B!;
            var c = pair.C!;
            var d = pair.D!;
            if (new[] { a, b, c, d }.All(result =>
                    result.Status == ExperimentResultStatus.Completed))
            {
                bothCompleted++;
            }

            var ledgerOnBaseline = Bool(VerifiedCodeChangePolicy.IsVerified(b)) -
                Bool(VerifiedCodeChangePolicy.IsVerified(a));
            var ledgerOnGraph = Bool(VerifiedCodeChangePolicy.IsVerified(d)) -
                Bool(VerifiedCodeChangePolicy.IsVerified(c));
            verified += ledgerOnGraph - ledgerOnBaseline;

            var firstPassLedgerBaseline =
                Bool(VerifiedCodeChangePolicy.IsVerified(b) && b.RetryCount == 0) -
                Bool(VerifiedCodeChangePolicy.IsVerified(a) && a.RetryCount == 0);
            var firstPassLedgerGraph =
                Bool(VerifiedCodeChangePolicy.IsVerified(d) && d.RetryCount == 0) -
                Bool(VerifiedCodeChangePolicy.IsVerified(c) && c.RetryCount == 0);
            firstPass += firstPassLedgerGraph - firstPassLedgerBaseline;

            tokens += (d.TotalTokens - c.TotalTokens) - (b.TotalTokens - a.TotalTokens);
            cost += (d.EstimatedCost - c.EstimatedCost) - (b.EstimatedCost - a.EstimatedCost);
        }

        return new ExperimentFactorialContrast
        {
            Name = "interaction",
            LeftArm = "(C-D)-(A-B)",
            RightArm = "(D-C)-(B-A)",
            PairedSampleCount = pairs.Count,
            BothCompletedCount = bothCompleted,
            VerifiedCodeChangeDelta = verified,
            FirstPassDelta = firstPass,
            TotalTokenDelta = tokens,
            EstimatedCostDelta = cost
        };
    }

    private static int Bool(bool value) => value ? 1 : 0;
}
