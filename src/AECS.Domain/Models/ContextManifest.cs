using System.Text.Json.Serialization;

namespace AECS.Domain.Models;

public sealed class ContextFileManifest
{
    public string Path { get; init; } = string.Empty;
    public string Sha256 { get; init; } = string.Empty;
    public string IncludedSha256 { get; init; } = string.Empty;
    public int OriginalCharacters { get; init; }
    public int IncludedCharacters { get; init; }
    public bool Truncated { get; init; }
    public List<string> Symbols { get; init; } = [];
}

public sealed class ContextManifest
{
    public string Id { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public string Source { get; init; } = "isolated-git-worktree";
    public string Strategy { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SemanticIndex { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SymbolGraphHash { get; init; }
    public int MaxTokens { get; init; }
    public int MaxCharacters { get; init; }
    public int EstimatedTokens { get; init; }
    public int TotalCharacters { get; init; }
    public int EligibleFileCount { get; init; }
    public int OmittedFileCount { get; init; }
    public bool Truncated { get; init; }
    public string ManifestHash { get; init; } = string.Empty;
    public List<ContextFileManifest> Files { get; init; } = [];
}
