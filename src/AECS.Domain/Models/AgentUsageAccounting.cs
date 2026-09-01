using System.Text.Json.Serialization;

namespace AECS.Domain.Models;

public static class AgentUsageAccountingSchema
{
    public const string Version = "aecs.agent-usage-accounting/v1";
    public const string Currency = "USD";
}

public sealed class AgentUsageAccounting
{
    public string SchemaVersion { get; init; } = AgentUsageAccountingSchema.Version;
    public string Adapter { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public int? EstimatedInputTokens { get; init; }
    public int? ReservedOutputTokens { get; init; }
    public int? ProviderInputTokens { get; init; }
    public int? ProviderOutputTokens { get; init; }
    public string ProviderRequestId { get; init; } = string.Empty;
    public decimal? RateCardEstimatedCostUsd { get; init; }
    public decimal? LocalResourceEstimatedCostUsd { get; init; }
    public bool CostComplete { get; init; }
    public string Currency { get; init; } = AgentUsageAccountingSchema.Currency;
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
    public List<AgentUsageAccounting> Components { get; init; } = [];

    [JsonIgnore]
    public int? InputTokenDivergence => Difference(
        ProviderInputTokens,
        EstimatedInputTokens);

    [JsonIgnore]
    public int? OutputTokenDivergence => Difference(
        ProviderOutputTokens,
        ReservedOutputTokens);

    [JsonIgnore]
    public decimal? AccountedCostUsd => CostComplete &&
        (RateCardEstimatedCostUsd is not null || LocalResourceEstimatedCostUsd is not null)
        ? (RateCardEstimatedCostUsd ?? 0m) + (LocalResourceEstimatedCostUsd ?? 0m)
        : null;

    [JsonIgnore]
    public string AccountedCostBasis => !CostComplete ||
        RateCardEstimatedCostUsd is null && LocalResourceEstimatedCostUsd is null
        ? "unavailable"
        : RateCardEstimatedCostUsd is not null && LocalResourceEstimatedCostUsd is not null
            ? "mixed-estimates"
            : RateCardEstimatedCostUsd is not null
                ? "rate-card-estimate"
                : "local-resource-estimate";

    private static int? Difference(int? final, int? estimate) =>
        final is not null && estimate is not null ? final - estimate : null;
}

public static class AgentUsageAccountingAggregation
{
    public static AgentUsageAccounting Aggregate(
        string adapter,
        string model,
        IEnumerable<AgentUsageAccounting> source)
    {
        var components = source.ToList();
        if (components.Count == 0)
            return new AgentUsageAccounting { Adapter = adapter, Model = model };
        var complete = components.All(component => component.CostComplete);
        return new AgentUsageAccounting
        {
            Adapter = adapter,
            Model = model,
            EstimatedInputTokens = SumComplete(
                components.Select(component => component.EstimatedInputTokens)),
            ReservedOutputTokens = SumComplete(
                components.Select(component => component.ReservedOutputTokens)),
            ProviderInputTokens = SumComplete(
                components.Select(component => component.ProviderInputTokens)),
            ProviderOutputTokens = SumComplete(
                components.Select(component => component.ProviderOutputTokens)),
            ProviderRequestId = Common(
                components.Select(component => component.ProviderRequestId)),
            RateCardEstimatedCostUsd = SumPresent(
                components.Select(component => component.RateCardEstimatedCostUsd)),
            LocalResourceEstimatedCostUsd = SumPresent(
                components.Select(component => component.LocalResourceEstimatedCostUsd)),
            CostComplete = complete,
            Currency = AgentUsageAccountingSchema.Currency,
            PricingTableVersion = Common(
                components.Select(component => component.PricingTableVersion)),
            PricingTableHash = Common(
                components.Select(component => component.PricingTableHash)),
            PricingEffectiveDate = CommonDate(
                components.Select(component => component.PricingEffectiveDate)),
            PricingSource = Common(components.Select(component => component.PricingSource)),
            PricingRateKind = Common(
                components.Select(component => component.PricingRateKind)),
            RateCardUsageBasis = Common(
                components.Select(component => component.RateCardUsageBasis)),
            LocalCostPolicyVersion = Common(
                components.Select(component => component.LocalCostPolicyVersion)),
            LocalPowerWatts = CommonDecimal(
                components.Select(component => component.LocalPowerWatts)),
            LocalElectricityUsdPerKwh = CommonDecimal(
                components.Select(component => component.LocalElectricityUsdPerKwh)),
            LocalHardwareCostUsd = CommonDecimal(
                components.Select(component => component.LocalHardwareCostUsd)),
            LocalHardwareLifetimeHours = CommonDecimal(
                components.Select(component => component.LocalHardwareLifetimeHours)),
            Components = components
        };
    }

    private static int? SumComplete(IEnumerable<int?> source)
    {
        var values = source.ToList();
        return values.All(value => value is not null)
            ? values.Sum(value => value!.Value)
            : null;
    }

    private static decimal? SumPresent(IEnumerable<decimal?> source)
    {
        var values = source.Where(value => value is not null).ToList();
        return values.Count == 0 ? null : values.Sum(value => value!.Value);
    }

    private static string Common(IEnumerable<string> source)
    {
        var values = source.Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        return values.Count switch
        {
            0 => string.Empty,
            1 => values[0],
            _ => "mixed"
        };
    }

    private static DateOnly? CommonDate(IEnumerable<DateOnly?> source)
    {
        var values = source.Where(value => value is not null)
            .Select(value => value!.Value).Distinct().ToList();
        return values.Count == 1 ? values[0] : null;
    }

    private static decimal? CommonDecimal(IEnumerable<decimal?> source)
    {
        var values = source.Where(value => value is not null)
            .Select(value => value!.Value).Distinct().ToList();
        return values.Count == 1 ? values[0] : null;
    }
}
