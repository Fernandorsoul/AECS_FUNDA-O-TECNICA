using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AECS.Domain.Models;

public static class ContextManifestSchema
{
    public const string LegacyVersion = "aecs.context-manifest/v1";
    public const string CurrentVersion = "aecs.context-manifest/v2";
    public const string StrategyVersion = "graph-ranked-token-budget/v1";
}

public sealed class ContextFileManifest
{
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string IncludedSha256 { get; init; } = string.Empty;
    public int OriginalCharacters { get; init; }
    public int IncludedCharacters { get; init; }
    public int OriginalTokens { get; init; }
    public int IncludedTokens { get; init; }
    public bool Truncated { get; init; }
    public int Rank { get; init; }
    public int Score { get; init; }
    public int Depth { get; init; }
    public string Relation { get; init; } = string.Empty;
    public List<string> Reasons { get; init; } = [];
    public List<string> Symbols { get; init; } = [];
}

public sealed class ContextSelectionManifest
{
    public string Path { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public int Rank { get; init; }
    public int Score { get; init; }
    public int Depth { get; init; }
    public string Relation { get; init; } = string.Empty;
    public List<string> RankingReasons { get; init; } = [];
    public string OriginalSha256 { get; init; } = string.Empty;
    public string IncludedSha256 { get; init; } = string.Empty;
    public int OriginalTokens { get; init; }
    public int IncludedTokens { get; init; }
}

public sealed class ContextManifest
{
    public string SchemaVersion { get; init; } = ContextManifestSchema.LegacyVersion;
    public string StrategyVersion { get; init; } = string.Empty;
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public string Source { get; init; } = "isolated-git-worktree";
    public string Strategy { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SemanticIndex { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SymbolGraphHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? RepositorySnapshotHash { get; init; }
    public string Adapter { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public string Tokenizer { get; init; } = string.Empty;
    public string TokenizerVersion { get; init; } = string.Empty;
    public bool ExactTokenCount { get; init; }
    public int ModelContextWindowTokens { get; init; }
    public int ReservedOutputTokens { get; init; }
    public int PromptOverheadTokens { get; init; }
    public int DependencyDepth { get; init; }
    public int MaxFileTokens { get; init; }
    public int MaxTokens { get; init; }
    public int MaxCharacters { get; init; }
    public int EstimatedTokens { get; init; }
    public int TotalCharacters { get; init; }
    public int EligibleFileCount { get; init; }
    public int OmittedFileCount { get; init; }
    public bool Truncated { get; init; }
    public string ManifestHash { get; init; } = string.Empty;
    public List<ContextFileManifest> Files { get; init; } = [];
    public List<ContextSelectionManifest> Selections { get; init; } = [];
}

public static class ContextManifestFingerprint
{
    public static string Create(ContextManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(manifest);
        var canonical = new
        {
            manifest.SchemaVersion,
            manifest.StrategyVersion,
            manifest.TaskId,
            manifest.BaselineCommit,
            manifest.Source,
            manifest.Strategy,
            manifest.SemanticIndex,
            manifest.SymbolGraphHash,
            manifest.RepositorySnapshotHash,
            manifest.Adapter,
            manifest.Model,
            manifest.Tokenizer,
            manifest.TokenizerVersion,
            manifest.ExactTokenCount,
            manifest.ModelContextWindowTokens,
            manifest.ReservedOutputTokens,
            manifest.PromptOverheadTokens,
            manifest.DependencyDepth,
            manifest.MaxFileTokens,
            manifest.MaxTokens,
            manifest.MaxCharacters,
            manifest.EstimatedTokens,
            manifest.TotalCharacters,
            manifest.EligibleFileCount,
            manifest.OmittedFileCount,
            manifest.Truncated,
            manifest.Files,
            manifest.Selections
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
