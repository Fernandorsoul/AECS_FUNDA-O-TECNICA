using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AECS.Domain.Models;

public static class HarnessManifestSchema
{
    public const string Version = "aecs.harness/v1";
}

/// <summary>
/// Immutable declaration of harness module configuration for one experiment
/// variant (plan §5.3). Identity is the canonical content hash; candidates may
/// vary evolvable modules only — security gates, isolation, and approval
/// criteria are bound to the immutable baseline below.
/// </summary>
public sealed class HarnessManifest
{
    public string SchemaVersion { get; init; } = HarnessManifestSchema.Version;
    public string VariantId { get; init; } = string.Empty;
    public string SecurityBaseline { get; init; } =
        HarnessManifestContract.ImmutableGatesVersion;
    public List<HarnessModuleConfig> Modules { get; init; } = [];
    public string ManifestHash { get; init; } = string.Empty;
}

public sealed class HarnessModuleConfig
{
    public string Module { get; init; } = string.Empty;
    public string Strategy { get; init; } = string.Empty;
    public string? PolicyRef { get; init; }
    public Dictionary<string, string> Parameters { get; init; } = new(StringComparer.Ordinal);
}

public static class HarnessManifestFingerprint
{
    public static string Create(HarnessManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.VariantId,
            manifest.SecurityBaseline,
            Modules = manifest.Modules
                .OrderBy(module => module.Module, StringComparer.Ordinal)
                .Select(module => new
                {
                    module.Module,
                    module.Strategy,
                    module.PolicyRef,
                    Parameters = module.Parameters
                        .OrderBy(pair => pair.Key, StringComparer.Ordinal)
                        .Select(pair => new { pair.Key, pair.Value })
                        .ToList()
                })
                .ToList()
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

/// <summary>
/// Sealing + validation for HarnessManifest. Evolvable module strategies are
/// explicitly allowlisted; anything that would alter security gates, isolation,
/// approval criteria, or the evaluation itself is rejected (plan §5.2).
/// </summary>
public static class HarnessManifestContract
{
    public const string ImmutableGatesVersion = "aecs.immutable-gates/v1";
    public const string ImmutableToolUsePolicy = "immutable-approved-policy";

    public static readonly IReadOnlyList<string> RequiredModules =
    [
        "agent-loop", "context", "observation", "tool-use", "completion"
    ];

    public static readonly IReadOnlyList<string> ImmutableGates =
    [
        "AgentSuccess", "Application", "NonEmptyChange", "Scope", "Budget",
        "Build", "Tests", "SecurityScan", "EB001-Architecture",
        AcceptanceCriteriaName, "ConstraintLedger"
    ];

    // Kept as a literal to avoid a Domain→Application dependency.
    private const string AcceptanceCriteriaName = "AcceptanceCriteria";

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedStrategies =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["agent-loop"] = new HashSet<string>(StringComparer.Ordinal) { "baseline" },
            ["context"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "baseline", "graph-ranked", "naive-path-order"
            },
            ["observation"] = new HashSet<string>(StringComparer.Ordinal) { "baseline" },
            ["tool-use"] = new HashSet<string>(StringComparer.Ordinal) { "preauthorized" },
            ["completion"] = new HashSet<string>(StringComparer.Ordinal) { "required-verifiers" }
        };

    private static readonly IReadOnlyDictionary<string, IReadOnlySet<string>> AllowedParameters =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal)
        {
            ["agent-loop"] = new HashSet<string>(StringComparer.Ordinal),
            ["context"] = new HashSet<string>(StringComparer.Ordinal)
            {
                "max_tokens", "dependency_depth", "constraintSectionPlacement"
            },
            ["observation"] = new HashSet<string>(StringComparer.Ordinal),
            ["tool-use"] = new HashSet<string>(StringComparer.Ordinal),
            ["completion"] = new HashSet<string>(StringComparer.Ordinal)
        };

    public static HarnessManifest Seal(HarnessManifest manifest)
    {
        Validate(manifest, requireManifestHash: false);
        var sealedManifest = new HarnessManifest
        {
            SchemaVersion = manifest.SchemaVersion,
            VariantId = manifest.VariantId,
            SecurityBaseline = manifest.SecurityBaseline,
            Modules = manifest.Modules,
            ManifestHash = HarnessManifestFingerprint.Create(manifest)
        };
        Validate(sealedManifest, requireManifestHash: true);
        return sealedManifest;
    }

    public static void Validate(HarnessManifest manifest, bool requireManifestHash = true)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var invalid =
            manifest.SchemaVersion != HarnessManifestSchema.Version ||
            string.IsNullOrWhiteSpace(manifest.VariantId) ||
            manifest.VariantId.Length > 128 ||
            manifest.SecurityBaseline != ImmutableGatesVersion ||
            manifest.Modules is null ||
            manifest.Modules.Count != RequiredModules.Count ||
            manifest.Modules.Select(module => module.Module)
                .Distinct(StringComparer.Ordinal).Count() != RequiredModules.Count ||
            RequiredModules.Any(required =>
                !manifest.Modules.Any(module =>
                    string.Equals(module.Module, required, StringComparison.Ordinal))) ||
            manifest.Modules.Any(module => module.Module is null || module.Strategy is null) ||
            manifest.Modules.Any(module =>
                !AllowedStrategies.TryGetValue(module.Module, out var allowed) ||
                !allowed.Contains(module.Strategy)) ||
            manifest.Modules.Any(module =>
                !AllowedParameters.TryGetValue(module.Module, out var allowedParams) ||
                module.Parameters is null ||
                module.Parameters.Keys.Any(key => !allowedParams.Contains(key))) ||
            manifest.Modules.Any(module =>
                module.Parameters is null ||
                module.Parameters.Any(pair =>
                    string.IsNullOrWhiteSpace(pair.Key) ||
                    string.IsNullOrWhiteSpace(pair.Value))) ||
            manifest.Modules.Any(module =>
                string.Equals(module.Module, "tool-use", StringComparison.Ordinal) &&
                !string.Equals(module.PolicyRef, ImmutableToolUsePolicy, StringComparison.Ordinal));

        if (invalid)
        {
            throw new InvalidOperationException(
                "Harness manifest is incomplete, inconsistent, or attempts to vary " +
                "a non-evolvable harness component.");
        }

        if (requireManifestHash &&
            (!manifest.ManifestHash.StartsWith("sha256:", StringComparison.Ordinal) ||
             !string.Equals(
                 manifest.ManifestHash,
                 HarnessManifestFingerprint.Create(manifest),
                 StringComparison.Ordinal)))
        {
            throw new InvalidOperationException(
                "Harness manifest hash is missing or does not match its content.");
        }
    }
}
