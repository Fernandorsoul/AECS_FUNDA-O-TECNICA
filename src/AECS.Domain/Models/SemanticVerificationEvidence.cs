namespace AECS.Domain.Models;

public static class SemanticVerificationEvidenceSchema
{
    public const string Version = "aecs.semantic-verification-evidence/v1";
}

public sealed class SemanticVerificationEvidence
{
    public string SchemaVersion { get; init; } = SemanticVerificationEvidenceSchema.Version;
    public string BaselineCommit { get; init; } = string.Empty;
    public string BaselineSnapshotHash { get; init; } = string.Empty;
    public string CandidateSnapshotHash { get; init; } = string.Empty;
    public string BaselineGraphHash { get; init; } = string.Empty;
    public string CandidateGraphHash { get; init; } = string.Empty;
    public List<string> ImpactedFiles { get; init; } = [];
    public List<SemanticFindingEvidence> Findings { get; init; } = [];
}

public sealed class SemanticFindingEvidence
{
    public string RuleId { get; init; } = string.Empty;
    public string SymbolId { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Baseline { get; init; } = string.Empty;
    public string Justification { get; init; } = string.Empty;
}
