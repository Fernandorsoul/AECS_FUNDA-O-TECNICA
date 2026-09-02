using System.Security.Cryptography;
using System.Text.Json;

namespace AECS.Infrastructure.AgentRuntime;

public sealed class ProviderPricingTable
{
    public string SchemaVersion { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string Currency { get; init; } = string.Empty;
    public DateTime CapturedAtUtc { get; init; }
    public List<ProviderTokenRate> Rates { get; init; } = [];
    public string Hash { get; init; } = string.Empty;
}

public sealed class ProviderTokenRate
{
    public string ModelPattern { get; init; } = string.Empty;
    public decimal InputUsdPerMillionTokens { get; init; }
    public decimal OutputUsdPerMillionTokens { get; init; }
    public DateOnly EffectiveDate { get; init; }
    public string Source { get; init; } = string.Empty;
    public string RateKind { get; init; } = string.Empty;
}

public sealed class ProviderPricingCatalog
{
    public const string SchemaVersion = "aecs.provider-pricing/v1";
    private const string ResourceName =
        "AECS.Infrastructure.AgentRuntime.provider-pricing.v1.json";
    private static readonly Lazy<ProviderPricingCatalog> Embedded = new(LoadEmbedded);
    private readonly ProviderPricingTable _table;

    public ProviderPricingCatalog(ProviderPricingTable table)
    {
        ArgumentNullException.ThrowIfNull(table);
        Validate(table);
        _table = table;
    }

    public static ProviderPricingCatalog Current => Embedded.Value;
    public ProviderPricingTable Table => _table;

    public ProviderTokenRate Resolve(string model) => _table.Rates
        .Where(rate => rate.ModelPattern == "*" || model.Contains(
            rate.ModelPattern,
            StringComparison.OrdinalIgnoreCase))
        .OrderBy(rate => rate.ModelPattern == "*" ? 1 : 0)
        .ThenByDescending(rate => rate.ModelPattern.Length)
        .First();

    public decimal Estimate(
        ProviderTokenRate rate,
        int inputTokens,
        int outputTokens) =>
        (Math.Max(0, inputTokens) * rate.InputUsdPerMillionTokens +
         Math.Max(0, outputTokens) * rate.OutputUsdPerMillionTokens) / 1_000_000m;

    private static ProviderPricingCatalog LoadEmbedded()
    {
        using var stream = typeof(ProviderPricingCatalog).Assembly
            .GetManifestResourceStream(ResourceName) ?? throw new InvalidOperationException(
                $"Embedded provider pricing table '{ResourceName}' was not found.");
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        var bytes = memory.ToArray();
        var table = JsonSerializer.Deserialize<ProviderPricingTable>(
            bytes,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true }) ??
            throw new InvalidOperationException("Provider pricing table is invalid JSON.");
        table = new ProviderPricingTable
        {
            SchemaVersion = table.SchemaVersion,
            Version = table.Version,
            Currency = table.Currency,
            CapturedAtUtc = table.CapturedAtUtc,
            Rates = table.Rates,
            Hash = CanonicalHash(bytes)
        };
        return new ProviderPricingCatalog(table);
    }

    private static string CanonicalHash(byte[] bytes)
    {
        using var document = JsonDocument.Parse(bytes);
        using var canonical = new MemoryStream();
        using (var writer = new Utf8JsonWriter(canonical))
        {
            document.RootElement.WriteTo(writer);
            writer.Flush();
        }
        return "sha256:" + Convert.ToHexString(SHA256.HashData(canonical.ToArray()))
            .ToLowerInvariant();
    }

    private static void Validate(ProviderPricingTable table)
    {
        if (table.SchemaVersion != SchemaVersion ||
            string.IsNullOrWhiteSpace(table.Version) ||
            table.Currency != "USD" ||
            table.CapturedAtUtc.Kind != DateTimeKind.Utc ||
            table.Rates is null || table.Rates.Count == 0 ||
            table.Rates.Count(rate => rate.ModelPattern == "*") != 1 ||
            table.Rates.Select(rate => rate.ModelPattern)
                .Distinct(StringComparer.OrdinalIgnoreCase).Count() != table.Rates.Count ||
            table.Rates.Any(rate =>
                string.IsNullOrWhiteSpace(rate.ModelPattern) ||
                rate.InputUsdPerMillionTokens < 0 ||
                rate.OutputUsdPerMillionTokens < 0 ||
                rate.EffectiveDate == default ||
                string.IsNullOrWhiteSpace(rate.Source) ||
                !Uri.TryCreate(rate.Source, UriKind.Absolute, out _) ||
                string.IsNullOrWhiteSpace(rate.RateKind)))
        {
            throw new InvalidOperationException(
                "Provider pricing table is incomplete or uses an unsupported schema.");
        }
    }
}
