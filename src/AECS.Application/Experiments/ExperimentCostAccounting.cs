using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application.Experiments;

public static class ExperimentCostAccountingSchema
{
    public const string ReconciliationVersion = "aecs.cost-reconciliation/v1";
    public const string AnalysisVersion = "aecs.cost-efficiency/v1";
    public const string VerifiedCodeChangeVersion = "aecs.vcc/v1";
    public const string Currency = "USD";
}

public sealed class ExperimentCostReconciliationLedger
{
    public string SchemaVersion { get; init; } =
        ExperimentCostAccountingSchema.ReconciliationVersion;
    public string Currency { get; init; } = ExperimentCostAccountingSchema.Currency;
    public string Source { get; init; } = string.Empty;
    public DateTime CapturedAtUtc { get; init; }
    public List<ExperimentCostReconciliationEntry> Entries { get; init; } = [];

    [JsonIgnore]
    public string Fingerprint => ExperimentCostReconciliationLoader.Fingerprint(this);
}

public sealed class ExperimentCostReconciliationEntry
{
    public string RunKey { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? EvidenceId { get; init; }
    public decimal CostUsd { get; init; }
    public string Reference { get; init; } = string.Empty;
}

public static class ExperimentCostReconciliationLoader
{
    public static ExperimentCostReconciliationLedger Load(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using var document = JsonDocument.Parse(
            File.ReadAllText(Path.GetFullPath(path)),
            new JsonDocumentOptions
            {
                AllowTrailingCommas = false,
                CommentHandling = JsonCommentHandling.Disallow,
                MaxDepth = 32
            });
        RejectDuplicateProperties(document.RootElement);
        var strictOptions = new JsonSerializerOptions(
            ExperimentDatasetLoader.SerializerOptions)
        {
            UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow
        };
        var ledger = document.RootElement.Deserialize<ExperimentCostReconciliationLedger>(
                strictOptions) ??
            throw new InvalidOperationException("Cost reconciliation ledger is empty.");
        Validate(ledger);
        return ledger;
    }

    public static void Validate(ExperimentCostReconciliationLedger ledger)
    {
        ArgumentNullException.ThrowIfNull(ledger);
        if (ledger.SchemaVersion != ExperimentCostAccountingSchema.ReconciliationVersion ||
            ledger.Currency != ExperimentCostAccountingSchema.Currency ||
            string.IsNullOrWhiteSpace(ledger.Source) || ledger.Source.Length > 500 ||
            ledger.CapturedAtUtc.Kind != DateTimeKind.Utc ||
            ledger.Entries is null ||
            ledger.Entries.Any(entry =>
                entry is null ||
                !ValidRunKey(entry.RunKey) ||
                entry.EvidenceId == Guid.Empty ||
                entry.CostUsd < 0 ||
                string.IsNullOrWhiteSpace(entry.Reference) ||
                entry.Reference.Length > 1000) ||
            ledger.Entries.Select(entry => entry.RunKey)
                .Distinct(StringComparer.Ordinal).Count() != ledger.Entries.Count)
        {
            throw new InvalidOperationException(
                "Cost reconciliation ledger is incomplete, duplicated, or uses an unsupported schema.");
        }
    }

    public static string Fingerprint(ExperimentCostReconciliationLedger ledger)
    {
        Validate(ledger);
        var json = JsonSerializer.Serialize(ledger, ExperimentDatasetLoader.SerializerOptions);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static bool ValidRunKey(string? value) => value is not null &&
        value.Length == 64 && value.All(character =>
            Uri.IsHexDigit(character) && !char.IsUpper(character));

    private static void RejectDuplicateProperties(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject())
            {
                if (!names.Add(property.Name))
                {
                    throw new InvalidOperationException(
                        $"Duplicate cost reconciliation property '{property.Name}' is not allowed.");
                }
                RejectDuplicateProperties(property.Value);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                RejectDuplicateProperties(item);
        }
    }
}

public sealed class ExperimentCostReconciliationMetadata
{
    public string SchemaVersion { get; init; } =
        ExperimentCostAccountingSchema.ReconciliationVersion;
    public string Currency { get; init; } = ExperimentCostAccountingSchema.Currency;
    public string LedgerFingerprint { get; init; } = string.Empty;
    public string Source { get; init; } = string.Empty;
    public DateTime? CapturedAtUtc { get; init; }
    public int MatchedEntries { get; init; }
}

public sealed class ExperimentCostRecord
{
    public string RunKey { get; init; } = string.Empty;
    public Guid? EvidenceId { get; init; }
    public ExperimentResultStatus Status { get; init; }
    public TaskDecision? Decision { get; init; }
    public bool IncludedInCpvc { get; init; }
    public string InclusionReason { get; init; } = string.Empty;
    public bool VerifiedCodeChange { get; init; }
    public string VccReason { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; }
    public string TaskId { get; init; } = string.Empty;
    public string Strategy { get; init; } = string.Empty;
    public string PeriodUtc { get; init; } = string.Empty;
    public int? EstimatedInputTokens { get; init; }
    public int? ReservedOutputTokens { get; init; }
    public int? ProviderInputTokens { get; init; }
    public int? ProviderOutputTokens { get; init; }
    public string ProviderRequestId { get; init; } = string.Empty;
    public int? InputTokenDivergence { get; init; }
    public int? OutputTokenDivergence { get; init; }
    public decimal? RateCardEstimatedCostUsd { get; init; }
    public decimal? LocalResourceEstimatedCostUsd { get; init; }
    public decimal? ReconciledCostUsd { get; init; }
    public decimal? EffectiveCostUsd { get; init; }
    public string CostBasis { get; init; } = string.Empty;
    public bool CostComplete { get; init; }
    public string PricingTableVersion { get; init; } = string.Empty;
    public string PricingTableHash { get; init; } = string.Empty;
    public DateOnly? PricingEffectiveDate { get; init; }
    public string PricingSource { get; init; } = string.Empty;
    public string PricingRateKind { get; init; } = string.Empty;
    public string RateCardUsageBasis { get; init; } = string.Empty;
    public string LocalCostPolicyVersion { get; init; } = string.Empty;
    public decimal? LocalPowerWatts { get; init; }
    public decimal? LocalElectricityUsdPerKwh { get; init; }
    public decimal? LocalHardwareCostUsd { get; init; }
    public decimal? LocalHardwareLifetimeHours { get; init; }
    public int RetryCount { get; init; }
    public int AccountingComponentCount { get; init; }
    public bool FallbackUsed { get; init; }
    public string ReconciliationSource { get; init; } = string.Empty;
    public string ReconciliationReference { get; init; } = string.Empty;
}

public sealed class ExperimentCostEfficiencyAnalysis
{
    public string SchemaVersion { get; init; } = ExperimentCostAccountingSchema.AnalysisVersion;
    public string Currency { get; init; } = ExperimentCostAccountingSchema.Currency;
    public string VccDefinitionVersion { get; init; } =
        ExperimentCostAccountingSchema.VerifiedCodeChangeVersion;
    public string InclusionRule { get; init; } =
        "All started executions are included, including rejected, human-review and failed " +
        "outcomes; skipped runs are excluded.";
    public string ConfidenceIntervalMethod { get; init; } =
        "Deterministic non-parametric bootstrap of total cost / VCC (2,000 resamples, 95%).";
    public List<ExperimentCostEfficiencyAggregate> Aggregates { get; init; } = [];
}

public sealed class ExperimentCostEfficiencyAggregate
{
    public string Dimension { get; init; } = string.Empty;
    public string Value { get; init; } = string.Empty;
    public int PlannedMembers { get; init; }
    public int SampleSize { get; init; }
    public int CompletedCount { get; init; }
    public int RejectedCount { get; init; }
    public int FailedCount { get; init; }
    public int SkippedCount { get; init; }
    public int VerifiedCodeChanges { get; init; }
    public int KnownCostCount { get; init; }
    public int MissingCostCount { get; init; }
    public double CostCoverage { get; init; }
    public decimal? TotalEffectiveCostUsd { get; init; }
    public decimal? CpvcUsd { get; init; }
    public double? CpvcConfidenceIntervalLower { get; init; }
    public double? CpvcConfidenceIntervalUpper { get; init; }
    public ExperimentMetricDistribution EffectiveCostDistribution { get; init; } = new();
    public List<string> MemberRunKeys { get; init; } = [];
    public List<string> IncludedRunKeys { get; init; } = [];
    public List<Guid> EvidenceIds { get; init; } = [];
}

public static class VerifiedCodeChangePolicy
{
    public static bool IsVerified(TaskExperimentResult result) =>
        Reason(result) == "included";

    public static string Reason(TaskExperimentResult result)
    {
        if (result.Status != ExperimentResultStatus.Completed)
            return $"status-{result.Status.ToString().ToLowerInvariant()}";
        if (result.Decision != TaskDecision.Verified)
            return "decision-not-verified";
        if (result.FilesChanged <= 0)
            return "no-candidate-files";
        if (!result.OriginalRepositoryUnchanged)
            return "original-repository-changed";
        if (result.ScopeViolationCount > 0)
            return "scope-violation";
        if (result.EvidenceId == Guid.Empty || string.IsNullOrWhiteSpace(result.EvidenceLocation))
            return "missing-origin-evidence";
        return "included";
    }
}

public static class ExperimentCostReconciler
{
    public static (List<ExperimentCostRecord> Records,
        ExperimentCostReconciliationMetadata Metadata) Build(
        IReadOnlyCollection<TaskExperimentResult> results,
        ExperimentCostReconciliationLedger? ledger,
        DateTime reportExecutedAtUtc,
        bool requireAllLedgerEntries = true)
    {
        if (ledger is not null)
            ExperimentCostReconciliationLoader.Validate(ledger);
        var entries = ledger?.Entries.ToDictionary(entry => entry.RunKey, StringComparer.Ordinal) ??
            new Dictionary<string, ExperimentCostReconciliationEntry>(StringComparer.Ordinal);
        var matched = new HashSet<string>(StringComparer.Ordinal);
        var records = new List<ExperimentCostRecord>();
        foreach (var result in results)
        {
            entries.TryGetValue(result.RunKey, out var reconciliation);
            if (reconciliation is not null)
            {
                if (result.Status == ExperimentResultStatus.Skipped)
                {
                    throw new InvalidOperationException(
                        $"Cost reconciliation entry '{result.RunKey}' targets a skipped run.");
                }
                if (reconciliation.EvidenceId is { } evidenceId &&
                    evidenceId != result.EvidenceId)
                {
                    throw new InvalidOperationException(
                        $"Cost reconciliation evidence does not match run '{result.RunKey}'.");
                }
                matched.Add(result.RunKey);
            }

            var accounting = result.UsageAccounting;
            var effectiveCost = reconciliation?.CostUsd ?? accounting?.AccountedCostUsd;
            var included = result.Status != ExperimentResultStatus.Skipped;
            records.Add(new ExperimentCostRecord
            {
                RunKey = result.RunKey,
                EvidenceId = result.EvidenceId == Guid.Empty ? null : result.EvidenceId,
                Status = result.Status,
                Decision = result.Decision,
                IncludedInCpvc = included,
                InclusionReason = included ? "executed" : "skipped-before-execution",
                VerifiedCodeChange = VerifiedCodeChangePolicy.IsVerified(result),
                VccReason = VerifiedCodeChangePolicy.Reason(result),
                Model = string.IsNullOrWhiteSpace(result.Model)
                    ? result.RequestedModel
                    : result.Model,
                Risk = result.Risk,
                TaskId = result.TaskId,
                Strategy = string.IsNullOrWhiteSpace(result.ActualContextStrategy)
                    ? result.ContextStrategy
                    : result.ActualContextStrategy,
                PeriodUtc = (result.FinishedAtUtc ?? reportExecutedAtUtc)
                    .ToUniversalTime().ToString("yyyy-MM-dd"),
                EstimatedInputTokens = accounting?.EstimatedInputTokens,
                ReservedOutputTokens = accounting?.ReservedOutputTokens,
                ProviderInputTokens = accounting?.ProviderInputTokens,
                ProviderOutputTokens = accounting?.ProviderOutputTokens,
                ProviderRequestId = accounting?.ProviderRequestId ?? string.Empty,
                InputTokenDivergence = accounting?.InputTokenDivergence,
                OutputTokenDivergence = accounting?.OutputTokenDivergence,
                RateCardEstimatedCostUsd = accounting?.RateCardEstimatedCostUsd,
                LocalResourceEstimatedCostUsd = accounting?.LocalResourceEstimatedCostUsd,
                ReconciledCostUsd = reconciliation?.CostUsd,
                EffectiveCostUsd = effectiveCost,
                CostBasis = reconciliation is not null
                    ? "reconciled"
                    : accounting?.AccountedCostBasis ?? "unavailable",
                CostComplete = effectiveCost is not null,
                PricingTableVersion = accounting?.PricingTableVersion ?? string.Empty,
                PricingTableHash = accounting?.PricingTableHash ?? string.Empty,
                PricingEffectiveDate = accounting?.PricingEffectiveDate,
                PricingSource = accounting?.PricingSource ?? string.Empty,
                PricingRateKind = accounting?.PricingRateKind ?? string.Empty,
                RateCardUsageBasis = accounting?.RateCardUsageBasis ?? string.Empty,
                LocalCostPolicyVersion = accounting?.LocalCostPolicyVersion ?? string.Empty,
                LocalPowerWatts = accounting?.LocalPowerWatts,
                LocalElectricityUsdPerKwh = accounting?.LocalElectricityUsdPerKwh,
                LocalHardwareCostUsd = accounting?.LocalHardwareCostUsd,
                LocalHardwareLifetimeHours = accounting?.LocalHardwareLifetimeHours,
                RetryCount = result.RetryCount,
                AccountingComponentCount = CountComponents(accounting),
                FallbackUsed = ContainsFallback(accounting),
                ReconciliationSource = reconciliation is null ? string.Empty : ledger!.Source,
                ReconciliationReference = reconciliation?.Reference ?? string.Empty
            });
        }

        if (requireAllLedgerEntries)
        {
            var unmatched = entries.Keys.Where(key => !matched.Contains(key)).ToList();
            if (unmatched.Count > 0)
            {
                throw new InvalidOperationException(
                    $"Cost reconciliation contains {unmatched.Count} unmatched run(s): " +
                    string.Join(", ", unmatched));
            }
        }

        return (records, new ExperimentCostReconciliationMetadata
        {
            LedgerFingerprint = ledger?.Fingerprint ?? string.Empty,
            Source = ledger?.Source ?? string.Empty,
            CapturedAtUtc = ledger?.CapturedAtUtc,
            MatchedEntries = matched.Count
        });
    }

    private static int CountComponents(AgentUsageAccounting? accounting) => accounting is null
        ? 0
        : accounting.Components.Count == 0
            ? 1
            : accounting.Components.Sum(CountComponents);

    private static bool ContainsFallback(AgentUsageAccounting? accounting) => accounting is not null &&
        (accounting.Adapter.Contains("Fallback", StringComparison.OrdinalIgnoreCase) ||
         accounting.Components.Any(ContainsFallback));
}

public static class ExperimentCostEfficiencyAnalyzer
{
    public static ExperimentCostEfficiencyAnalysis Analyze(
        IReadOnlyCollection<ExperimentCostRecord> records)
    {
        var aggregates = new List<ExperimentCostEfficiencyAggregate>
        {
            Aggregate("overall", "all", records)
        };
        AddGroups(aggregates, "model", records, record => record.Model);
        AddGroups(aggregates, "risk", records, record => record.Risk.ToString());
        AddGroups(aggregates, "task", records, record => record.TaskId);
        AddGroups(aggregates, "strategy", records, record => record.Strategy);
        AddGroups(aggregates, "period", records, record => record.PeriodUtc);
        return new ExperimentCostEfficiencyAnalysis { Aggregates = aggregates };
    }

    private static void AddGroups(
        List<ExperimentCostEfficiencyAggregate> target,
        string dimension,
        IReadOnlyCollection<ExperimentCostRecord> records,
        Func<ExperimentCostRecord, string> selector)
    {
        target.AddRange(records.GroupBy(selector, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => Aggregate(dimension, group.Key, group.ToList())));
    }

    private static ExperimentCostEfficiencyAggregate Aggregate(
        string dimension,
        string value,
        IReadOnlyCollection<ExperimentCostRecord> members)
    {
        var sample = members.Where(member => member.IncludedInCpvc).ToList();
        var known = sample.Where(member => member.EffectiveCostUsd is not null).ToList();
        var complete = sample.Count > 0 && known.Count == sample.Count;
        var vcc = sample.Count(member => member.VerifiedCodeChange);
        decimal? total = complete
            ? sample.Sum(member => member.EffectiveCostUsd!.Value)
            : null;
        decimal? cpvc = total is not null && vcc > 0 ? total.Value / vcc : null;
        var interval = complete && vcc > 0
            ? BootstrapCpvc(dimension + "\n" + value, sample)
            : (Lower: (double?)null, Upper: (double?)null);
        return new ExperimentCostEfficiencyAggregate
        {
            Dimension = dimension,
            Value = value,
            PlannedMembers = members.Count,
            SampleSize = sample.Count,
            CompletedCount = sample.Count(member =>
                member.Status == ExperimentResultStatus.Completed),
            RejectedCount = sample.Count(member =>
                member.Status == ExperimentResultStatus.Completed &&
                member.Decision == TaskDecision.Rejected),
            FailedCount = sample.Count(member => member.Status == ExperimentResultStatus.Failed),
            SkippedCount = members.Count(member => member.Status == ExperimentResultStatus.Skipped),
            VerifiedCodeChanges = vcc,
            KnownCostCount = known.Count,
            MissingCostCount = sample.Count - known.Count,
            CostCoverage = sample.Count == 0 ? 0 : known.Count / (double)sample.Count,
            TotalEffectiveCostUsd = total,
            CpvcUsd = cpvc,
            CpvcConfidenceIntervalLower = interval.Lower,
            CpvcConfidenceIntervalUpper = interval.Upper,
            EffectiveCostDistribution = ExperimentAnalyzer.Distribution(
                known.Select(member => (double)member.EffectiveCostUsd!.Value)),
            MemberRunKeys = members.Select(member => member.RunKey)
                .OrderBy(key => key, StringComparer.Ordinal).ToList(),
            IncludedRunKeys = sample.Select(member => member.RunKey)
                .OrderBy(key => key, StringComparer.Ordinal).ToList(),
            EvidenceIds = sample.Where(member => member.EvidenceId is not null)
                .Select(member => member.EvidenceId!.Value).Distinct().Order().ToList()
        };
    }

    private static (double? Lower, double? Upper) BootstrapCpvc(
        string groupIdentity,
        IReadOnlyList<ExperimentCostRecord> sample)
    {
        if (sample.Count == 1)
        {
            var exact = (double)sample[0].EffectiveCostUsd!.Value;
            return (exact, exact);
        }
        var estimates = new List<double>(2000);
        for (var iteration = 0; iteration < 2000; iteration++)
        {
            decimal cost = 0;
            var vcc = 0;
            for (var draw = 0; draw < sample.Count; draw++)
            {
                var index = DeterministicIndex(groupIdentity, iteration, draw, sample.Count);
                var member = sample[index];
                cost += member.EffectiveCostUsd!.Value;
                if (member.VerifiedCodeChange)
                    vcc++;
            }
            if (vcc > 0)
                estimates.Add((double)(cost / vcc));
        }
        if (estimates.Count == 0)
            return (null, null);
        estimates.Sort();
        return (Quantile(estimates, 0.025), Quantile(estimates, 0.975));
    }

    private static int DeterministicIndex(
        string identity,
        int iteration,
        int draw,
        int count)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(
            $"{identity}\n{iteration}\n{draw}"));
        return (int)(BitConverter.ToUInt32(bytes, 0) % (uint)count);
    }

    private static double Quantile(IReadOnlyList<double> ordered, double probability)
    {
        var position = (ordered.Count - 1) * probability;
        var lower = (int)Math.Floor(position);
        var upper = (int)Math.Ceiling(position);
        return lower == upper
            ? ordered[lower]
            : ordered[lower] + (ordered[upper] - ordered[lower]) * (position - lower);
    }
}
