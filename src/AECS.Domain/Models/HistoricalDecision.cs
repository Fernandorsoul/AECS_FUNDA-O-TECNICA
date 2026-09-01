using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AECS.Domain.Models;

public static class HistoricalDecisionSchema
{
    public const string DecisionVersion = "aecs.historical-decision/v1";
    public const string SuppressionVersion = "aecs.historical-decision-suppression/v1";
    public const string VerificationEvidenceVersion =
        "aecs.historical-decision-verification/v1";
}
public enum HistoricalDecisionType
{
    Adr,
    Incident,
    Decision,
    Policy
}

public enum HistoricalDecisionEnforcement
{
    Advisory,
    Blocking
}

public enum HistoricalDecisionReviewStatus
{
    Draft,
    Approved,
    Rejected
}

public enum HistoricalDecisionReviewAuthority
{
    None,
    Human,
    Policy
}

public enum HistoricalPatternKind
{
    SymbolName,
    TypeName,
    NamespacePrefix,
    ConstructsType,
    ReferencesSymbol,
    ImplementsType,
    InheritsType,
    ProjectReference
}

public enum HistoricalDecisionSelectionStatus
{
    Selected,
    NoHistory,
    Ambiguous,
    Unavailable
}

public sealed class HistoricalDecision
{
    public string SchemaVersion { get; init; } = HistoricalDecisionSchema.DecisionVersion;
    public string Id { get; init; } = string.Empty;
    public int Version { get; init; } = 1;
    public HistoricalDecisionType Type { get; init; }
    public string Source { get; init; } = string.Empty;
    public string SourceVersion { get; init; } = string.Empty;
    public string SourceHash { get; init; } = string.Empty;
    public string Authority { get; init; } = string.Empty;
    public DateTime ValidFrom { get; init; } = DateTime.UnixEpoch;
    public DateTime? ValidUntil { get; init; }
    public List<HistoricalDecisionPattern> ProhibitedPatterns { get; init; } = [];
    public List<HistoricalDecisionPattern> RequiredPatterns { get; init; } = [];
    public string Justification { get; init; } = string.Empty;
    public HistoricalDecisionEnforcement Enforcement { get; init; }
    public bool ExtractedHeuristically { get; init; }
    public HistoricalDecisionScope Scope { get; init; } = new();
    public HistoricalDecisionReview Review { get; init; } = new();
    public string ContentHash { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UnixEpoch;
}

public sealed class HistoricalDecisionPattern
{
    public HistoricalPatternKind Kind { get; init; }
    public string Value { get; init; } = string.Empty;
}

public sealed class HistoricalDecisionScope
{
    public List<string> ProjectPaths { get; init; } = [];
    public List<string> NamespacePrefixes { get; init; } = [];
    public List<string> SymbolKinds { get; init; } = [];
}

public sealed class HistoricalDecisionReview
{
    public HistoricalDecisionReviewStatus Status { get; init; }
    public HistoricalDecisionReviewAuthority Authority { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTime? ReviewedAt { get; init; }
}

public sealed class HistoricalDecisionSuppression
{
    public string SchemaVersion { get; init; } =
        HistoricalDecisionSchema.SuppressionVersion;
    public string Id { get; init; } = string.Empty;
    public int Version { get; init; } = 1;
    public string DecisionId { get; init; } = string.Empty;
    public int DecisionVersion { get; init; }
    public string Actor { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public string SymbolId { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UnixEpoch;
    public DateTime ExpiresAt { get; init; } = DateTime.UnixEpoch;
    public string ContentHash { get; init; } = string.Empty;
}

public sealed class HistoricalDecisionSelection
{
    public HistoricalDecisionSelectionStatus Status { get; init; }
    public DateTime EvaluatedAt { get; init; } = DateTime.UnixEpoch;
    public string Message { get; init; } = string.Empty;
    public List<HistoricalDecision> Decisions { get; init; } = [];
    public List<HistoricalDecisionSuppression> Suppressions { get; init; } = [];
}

public sealed class HistoricalDecisionVerificationEvidence
{
    public string SchemaVersion { get; init; } =
        HistoricalDecisionSchema.VerificationEvidenceVersion;
    public HistoricalDecisionSelectionStatus Status { get; init; }
    public DateTime EvaluatedAt { get; init; } = DateTime.UnixEpoch;
    public string Message { get; init; } = string.Empty;
    public List<HistoricalDecision> Decisions { get; init; } = [];
    public List<HistoricalDecisionSuppression> Suppressions { get; init; } = [];
    public List<HistoricalConflictEvidence> Conflicts { get; init; } = [];
}

public sealed class HistoricalConflictEvidence
{
    public string RuleId { get; init; } = string.Empty;
    public string DecisionId { get; init; } = string.Empty;
    public int DecisionVersion { get; init; }
    public string Source { get; init; } = string.Empty;
    public string SourceVersion { get; init; } = string.Empty;
    public string SourceHash { get; init; } = string.Empty;
    public string Authority { get; init; } = string.Empty;
    public string SymbolId { get; init; } = string.Empty;
    public string Symbol { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string Severity { get; init; } = string.Empty;
    public string Pattern { get; init; } = string.Empty;
    public string Justification { get; init; } = string.Empty;
    public bool Suppressed { get; init; }
    public string SuppressionId { get; init; } = string.Empty;
    public int? SuppressionVersion { get; init; }
}

public static class HistoricalDecisionFingerprint
{
    public static string Create(HistoricalDecision decision) => Hash(JsonSerializer.Serialize(
        new
        {
            decision.SchemaVersion,
            decision.Id,
            decision.Version,
            decision.Type,
            decision.Source,
            decision.SourceVersion,
            decision.SourceHash,
            decision.Authority,
            decision.ValidFrom,
            decision.ValidUntil,
            decision.ProhibitedPatterns,
            decision.RequiredPatterns,
            decision.Justification,
            decision.Enforcement,
            decision.ExtractedHeuristically,
            decision.Scope,
            decision.Review,
            decision.CreatedAt
        }));

    public static string Create(HistoricalDecisionSuppression suppression) =>
        Hash(JsonSerializer.Serialize(new
        {
            suppression.SchemaVersion,
            suppression.Id,
            suppression.Version,
            suppression.DecisionId,
            suppression.DecisionVersion,
            suppression.Actor,
            suppression.Reason,
            suppression.SymbolId,
            suppression.FilePath,
            suppression.CreatedAt,
            suppression.ExpiresAt
        }));

    public static string StorageKey(string id) => Hash(id)[7..];

    private static string Hash(string value) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value)))
            .ToLowerInvariant();
}
