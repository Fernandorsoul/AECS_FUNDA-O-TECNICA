using AECS.Domain.Models;

namespace AECS.Application.Experiments;

/// <summary>
/// Derives the harness manifest that describes an experiment variant's actual
/// module configuration. Every variant is bound to the same immutable security
/// baseline; only the context strategy varies across the P4 paired set
/// (plan §5.4: one variable per comparison).
/// </summary>
public static class HarnessVariantBinding
{
    private static readonly IReadOnlyDictionary<string, string> ContextStrategyAliases =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [""] = "baseline",
            ["baseline"] = "baseline",
            ["naive-path-order"] = "naive-path-order",
            ["ordinal-path-order"] = "naive-path-order",
            ["path-order"] = "naive-path-order",
            ["graph-ranked"] = "graph-ranked",
            ["graph"] = "graph-ranked",
            ["semantic"] = "graph-ranked"
        };

    public static HarnessManifest ForVariant(ExperimentVariantDefinition variant)
    {
        ArgumentNullException.ThrowIfNull(variant);
        var contextStrategy = CanonicalContextStrategy(variant.ContextStrategy);
        var contextParameters = new Dictionary<string, string>(StringComparer.Ordinal);
        if (variant.Context.MaxTokens > 0)
        {
            contextParameters["max_tokens"] = variant.Context.MaxTokens.ToString();
        }

        if (variant.Context.DependencyDepth > 0)
        {
            contextParameters["dependency_depth"] = variant.Context.DependencyDepth.ToString();
        }

        if (variant.Parameters.TryGetValue("constraintSectionPlacement", out var placement) &&
            !string.IsNullOrWhiteSpace(placement))
        {
            contextParameters["constraintSectionPlacement"] = placement;
        }

        return HarnessManifestContract.Seal(new HarnessManifest
        {
            VariantId = variant.Id,
            Modules =
            [
                new HarnessModuleConfig { Module = "agent-loop", Strategy = "baseline" },
                new HarnessModuleConfig
                {
                    Module = "context",
                    Strategy = contextStrategy,
                    Parameters = contextParameters
                },
                new HarnessModuleConfig { Module = "observation", Strategy = "baseline" },
                new HarnessModuleConfig
                {
                    Module = "tool-use",
                    Strategy = "preauthorized",
                    PolicyRef = HarnessManifestContract.ImmutableToolUsePolicy
                },
                new HarnessModuleConfig { Module = "completion", Strategy = "required-verifiers" }
            ]
        });
    }

    /// <summary>
    /// Records the manifest when the variant uses a recognized harness strategy.
    /// Synthetic/unrecognized strategies (unit-test fixtures) yield null instead
    /// of failing the experiment run — unknown configurations are simply not
    /// described by a manifest.
    /// </summary>
    public static HarnessManifest? ForVariantOrNull(ExperimentVariantDefinition variant)
    {
        try
        {
            return ForVariant(variant);
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static string CanonicalContextStrategy(string? strategy)
    {
        var key = strategy ?? string.Empty;
        return ContextStrategyAliases.TryGetValue(key, out var canonical)
            ? canonical
            : key;
    }
}
