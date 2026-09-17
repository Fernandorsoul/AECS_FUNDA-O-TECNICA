using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AECS.Domain.Models;

public static class ConstraintLedgerSchema
{
    public const string RecordVersion = "aecs.constraint-record/v1";
    public const string SetRefVersion = "aecs.constraint-set/v1";
    public const string ConflictVersion = "aecs.constraint-conflict/v1";
    public const string EvidenceVersion = "aecs.constraint-ledger-evidence/v1";
}

public enum ConstraintKind
{
    SecurityPolicy,
    ScopeBoundary,
    BudgetLimit,
    VerificationRequirement,
    ApprovalRequirement,
    ProcessInvariant,
    ArchitecturalRule,
    AcceptanceCriterion
}

public enum ConstraintStatus
{
    Active,
    Superseded,
    Revoked,
    Conflicted,
    PendingVerification
}

public enum ConstraintAuthority
{
    TaskContract,
    RuntimePolicy,
    SecurityPolicy,
    HumanDecision,
    ArchitecturalDecision
}

public enum ConstraintOrigin
{
    TaskContractYaml,
    RuntimeConfiguration,
    HistoricalDecision,
    HumanOverride,
    SystemInferred
}

public enum ConstraintVerifiability
{
    Deterministic,
    Assisted,
    Manual,
    ProcessInvariant
}

/// <summary>
/// A single versioned constraint with identity, provenance, and lifecycle tracking.
/// Each revision creates a new immutable record; supersession preserves history.
/// </summary>
public sealed class ConstraintRecord
{
    public string SchemaVersion { get; init; } = ConstraintLedgerSchema.RecordVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public string RequirementKey { get; init; } = string.Empty;
    public int Revision { get; init; } = 1;
    public string Description { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty;
    public ConstraintKind Kind { get; init; }
    public ConstraintStatus Status { get; init; } = ConstraintStatus.Active;
    public ConstraintAuthority Authority { get; init; }
    public ConstraintOrigin Origin { get; init; }
    public ConstraintVerifiability Verifiability { get; init; }
    public Guid? SupersedesId { get; init; }
    public string? VerifierName { get; init; }
    public string RepositorySnapshotId { get; init; } = string.Empty;
    public string ContentHash { get; init; } = string.Empty;
    public DateTime ValidFrom { get; init; } = DateTime.UtcNow;
    public DateTime? ValidUntil { get; init; }
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Immutable reference to a specific set of constraints with a canonical hash.
/// Recorded in execution evidence to bind a run to its applicable constraints.
/// </summary>
public sealed class ConstraintSetRef
{
    public string SchemaVersion { get; init; } = ConstraintLedgerSchema.SetRefVersion;
    public string Id { get; init; } = string.Empty;
    public int Revision { get; init; }
    public string CanonicalSha256 { get; init; } = string.Empty;
    public int ActiveCount { get; init; }
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Record of a conflict between two constraints of equal authority.
/// Blocks approval until explicitly resolved by a higher authority.
/// </summary>
public sealed class ConstraintConflict
{
    public string SchemaVersion { get; init; } = ConstraintLedgerSchema.ConflictVersion;
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ConstraintAId { get; init; }
    public int ConstraintARevision { get; init; }
    public Guid ConstraintBId { get; init; }
    public int ConstraintBRevision { get; init; }
    public string Reason { get; init; } = string.Empty;
    public bool Resolved { get; init; }
    public string? ResolvedByActor { get; init; }
    public string? ResolutionReason { get; init; }
    public DateTime DetectedAt { get; init; } = DateTime.UtcNow;
    public DateTime? ResolvedAt { get; init; }
}

public static class ConstraintLedgerFingerprint
{
    public static string Create(ConstraintRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        var canonical = new
        {
            record.SchemaVersion,
            record.Id,
            record.RequirementKey,
            record.Revision,
            record.Description,
            record.Scope,
            Kind = record.Kind.ToString(),
            Status = record.Status.ToString(),
            Authority = record.Authority.ToString(),
            Origin = record.Origin.ToString(),
            Verifiability = record.Verifiability.ToString(),
            record.SupersedesId,
            record.VerifierName,
            record.RepositorySnapshotId,
            record.ValidFrom,
            record.ValidUntil
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }

    public static string CreateSetHash(IReadOnlyList<ConstraintRecord> activeRecords)
    {
        ArgumentNullException.ThrowIfNull(activeRecords);
        var ordered = activeRecords
            .Where(r => r.Status == ConstraintStatus.Active)
            .OrderBy(r => r.Id)
            .ThenBy(r => r.Revision)
            .Select(r => new { r.Id, r.Revision, r.ContentHash })
            .ToList();
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(ordered));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}

/// <summary>
/// Sealing and validation for ConstraintRecord, following the same pattern
/// as HistoricalDecisionContract.
/// </summary>
public static class ConstraintRecordContract
{
    public static ConstraintRecord Seal(ConstraintRecord record)
    {
        Validate(record, requireContentHash: false);
        var sealedRecord = Copy(record, ConstraintLedgerFingerprint.Create(record));
        Validate(sealedRecord, requireContentHash: true);
        return sealedRecord;
    }

    public static void Validate(ConstraintRecord record, bool requireContentHash = true)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (record.SchemaVersion != ConstraintLedgerSchema.RecordVersion ||
            record.Id == Guid.Empty ||
            string.IsNullOrWhiteSpace(record.RequirementKey) ||
            record.RequirementKey.Length > 200 ||
            record.Revision <= 0 ||
            string.IsNullOrWhiteSpace(record.Description) ||
            record.Description.Length > 4000 ||
            record.Scope.Length > 1000 ||
            !Enum.IsDefined(record.Kind) ||
            !Enum.IsDefined(record.Status) ||
            !Enum.IsDefined(record.Authority) ||
            !Enum.IsDefined(record.Origin) ||
            !Enum.IsDefined(record.Verifiability) ||
            record.SupersedesId == Guid.Empty ||
            record.VerifierName?.Length > 200 ||
            string.IsNullOrWhiteSpace(record.RepositorySnapshotId) ||
            record.RepositorySnapshotId.Length > 200 ||
            !IsUtc(record.ValidFrom) ||
            record.ValidUntil is { } validUntil &&
                (!IsUtc(validUntil) || validUntil <= record.ValidFrom) ||
            !IsUtc(record.CreatedAt) ||
            requireContentHash &&
                (!IsSha256(record.ContentHash) ||
                 !string.Equals(
                     record.ContentHash,
                     ConstraintLedgerFingerprint.Create(record),
                     StringComparison.Ordinal)) ||
            !requireContentHash &&
                !string.IsNullOrEmpty(record.ContentHash) &&
                !string.Equals(
                    record.ContentHash,
                    ConstraintLedgerFingerprint.Create(record),
                    StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Constraint record is incomplete, inconsistent, or uses an unsupported schema.");
        }
    }

    private static bool IsUtc(DateTime value) => value.Kind == DateTimeKind.Utc;

    private static bool IsSha256(string value) =>
        value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => Uri.IsHexDigit(character) && !char.IsUpper(character));

    private static ConstraintRecord Copy(
        ConstraintRecord source,
        string contentHash) => new()
        {
            SchemaVersion = source.SchemaVersion,
            Id = source.Id,
            RequirementKey = source.RequirementKey,
            Revision = source.Revision,
            Description = source.Description,
            Scope = source.Scope,
            Kind = source.Kind,
            Status = source.Status,
            Authority = source.Authority,
            Origin = source.Origin,
            Verifiability = source.Verifiability,
            SupersedesId = source.SupersedesId,
            VerifierName = source.VerifierName,
            RepositorySnapshotId = source.RepositorySnapshotId,
            ContentHash = contentHash,
            ValidFrom = source.ValidFrom,
            ValidUntil = source.ValidUntil,
            CreatedAt = source.CreatedAt
        };
}

/// <summary>
/// Outcome of evaluating a single active constraint during a run.
/// PendingReview is never reported as satisfied by inference (plan §4.1).
/// </summary>
public enum ConstraintAssessmentOutcome
{
    Satisfied,
    Violated,
    PendingReview
}

public sealed class ConstraintAssessment
{
    public string RequirementKey { get; init; } = string.Empty;
    public int Revision { get; init; }
    public ConstraintKind Kind { get; init; }
    public ConstraintVerifiability Verifiability { get; init; }
    public string? VerifierName { get; init; }
    public ConstraintAssessmentOutcome Outcome { get; init; }
    public string Detail { get; init; } = string.Empty;
}

/// <summary>
/// Per-requirement evaluation evidence carried by the ConstraintLedger verifier
/// result and sealed inside the execution evidence envelope.
/// </summary>
public sealed class ConstraintLedgerVerificationEvidence
{
    public string SchemaVersion { get; init; } = ConstraintLedgerSchema.EvidenceVersion;
    public string SetId { get; init; } = string.Empty;
    public int SetRevision { get; init; }
    public string SetCanonicalSha256 { get; init; } = string.Empty;
    public int ActiveCount { get; init; }
    public int UnresolvedConflictCount { get; init; }
    public List<ConstraintAssessment> Assessments { get; init; } = [];
    public DateTime EvaluatedAt { get; init; } = DateTime.UtcNow;

    public bool HasViolations => UnresolvedConflictCount > 0 ||
        Assessments.Any(assessment => assessment.Outcome == ConstraintAssessmentOutcome.Violated);

    public bool HasPendingReview =>
        Assessments.Any(assessment => assessment.Outcome == ConstraintAssessmentOutcome.PendingReview);
}
