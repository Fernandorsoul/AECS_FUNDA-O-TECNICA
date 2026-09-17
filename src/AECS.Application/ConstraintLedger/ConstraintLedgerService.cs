using AECS.Domain.Models;

namespace AECS.Application.ConstraintLedger;

/// <summary>
/// In-memory constraint ledger that manages constraint lifecycle:
/// ingest, supersede, revoke, conflict detection, and set resolution.
/// Thread-safe for single-writer/multiple-reader scenarios.
/// </summary>
public sealed class ConstraintLedgerService
{
    private readonly List<ConstraintRecord> _records = [];
    private readonly List<ConstraintConflict> _conflicts = [];

    public IReadOnlyList<ConstraintRecord> AllRecords => _records;
    public IReadOnlyList<ConstraintConflict> AllConflicts => _conflicts;

    /// <summary>
    /// Ingests a new constraint. Seals it with a content hash.
    /// If a constraint with the same RequirementKey already exists at the same or higher
    /// revision with the same or higher authority, a conflict is recorded.
    /// </summary>
    public ConstraintRecord Ingest(ConstraintRecord record)
    {
        var sealedRecord = ConstraintRecordContract.Seal(record);

        var sameIdentity = _records.FirstOrDefault(r => r.Id == sealedRecord.Id);
        if (sameIdentity is not null)
        {
            if (string.Equals(
                    sameIdentity.ContentHash,
                    sealedRecord.ContentHash,
                    StringComparison.Ordinal))
            {
                return sameIdentity;
            }

            throw new InvalidOperationException(
                $"Constraint id '{sealedRecord.Id}' already exists with different content.");
        }

        var existing = _records
            .Where(r => r.RequirementKey == sealedRecord.RequirementKey &&
                        r.Status is ConstraintStatus.Active or ConstraintStatus.PendingVerification)
            .ToList();

        foreach (var prior in existing)
        {
            if (prior.Authority < sealedRecord.Authority)
            {
                // Higher authority supersedes lower — auto-supersede
                SupersedeInternal(prior.Id, sealedRecord.Id, "Auto-superseded by higher authority");
            }
            else if (prior.Authority == sealedRecord.Authority &&
                     prior.Revision < sealedRecord.Revision)
            {
                // Same authority, higher revision — auto-supersede
                SupersedeInternal(prior.Id, sealedRecord.Id, "Superseded by newer revision");
            }
            else if (prior.Authority == sealedRecord.Authority &&
                     prior.Revision == sealedRecord.Revision &&
                     prior.Id != sealedRecord.Id)
            {
                // Same authority, same revision, different ID — conflict
                RecordConflict(prior, sealedRecord,
                    $"Duplicate constraint '{sealedRecord.RequirementKey}' " +
                    $"at revision {sealedRecord.Revision} with same authority");
            }
            else if (prior.Authority > sealedRecord.Authority)
            {
                // Lower authority trying to add — conflict
                RecordConflict(prior, sealedRecord,
                    $"Lower authority ({sealedRecord.Authority}) cannot override " +
                    $"higher authority ({prior.Authority}) for '{sealedRecord.RequirementKey}'");
            }
        }

        _records.Add(sealedRecord);
        return sealedRecord;
    }

    /// <summary>
    /// Explicitly supersedes an existing constraint with a new one.
    /// Preserves the historical trail.
    /// </summary>
    public ConstraintRecord Supersede(Guid existingId, ConstraintRecord replacement)
    {
        var existing = _records.FirstOrDefault(r => r.Id == existingId)
            ?? throw new InvalidOperationException(
                $"Cannot supersede: constraint {existingId} not found.");

        if (existing.Status is ConstraintStatus.Superseded or ConstraintStatus.Revoked)
            throw new InvalidOperationException(
                $"Cannot supersede constraint {existingId} with status {existing.Status}.");

        if (replacement.Authority < existing.Authority)
            throw new InvalidOperationException(
                $"Replacement authority ({replacement.Authority}) is lower than " +
                $"existing ({existing.Authority}). Only equal or higher authority can supersede.");

        var sealedReplacement = ConstraintRecordContract.Seal(new ConstraintRecord
        {
            SchemaVersion = replacement.SchemaVersion,
            Id = replacement.Id,
            RequirementKey = existing.RequirementKey,
            Revision = existing.Revision + 1,
            Description = replacement.Description,
            Scope = replacement.Scope,
            Kind = replacement.Kind,
            Status = replacement.Status,
            Authority = replacement.Authority,
            Origin = replacement.Origin,
            Verifiability = replacement.Verifiability,
            SupersedesId = existingId,
            VerifierName = replacement.VerifierName,
            RepositorySnapshotId = replacement.RepositorySnapshotId,
            ValidFrom = replacement.ValidFrom,
            ValidUntil = replacement.ValidUntil,
            CreatedAt = replacement.CreatedAt
        });

        SupersedeInternal(existingId, sealedReplacement.Id, "Explicit supersession");
        _records.Add(sealedReplacement);
        return sealedReplacement;
    }

    /// <summary>
    /// Revokes a constraint. Requires equal or higher authority.
    /// </summary>
    public ConstraintRecord Revoke(Guid constraintId, ConstraintAuthority revokerAuthority, string reason)
    {
        var existing = _records.FirstOrDefault(r => r.Id == constraintId)
            ?? throw new InvalidOperationException(
                $"Cannot revoke: constraint {constraintId} not found.");

        if (existing.Status is ConstraintStatus.Revoked)
            throw new InvalidOperationException(
                $"Constraint {constraintId} is already revoked.");

        if (revokerAuthority < existing.Authority)
            throw new InvalidOperationException(
                $"Revoker authority ({revokerAuthority}) is lower than " +
                $"constraint authority ({existing.Authority}).");

        var revoked = new ConstraintRecord
        {
            SchemaVersion = existing.SchemaVersion,
            Id = existing.Id,
            RequirementKey = existing.RequirementKey,
            Revision = existing.Revision + 1,
            Description = existing.Description,
            Scope = existing.Scope,
            Kind = existing.Kind,
            Status = ConstraintStatus.Revoked,
            Authority = existing.Authority,
            Origin = existing.Origin,
            Verifiability = existing.Verifiability,
            SupersedesId = existing.SupersedesId,
            VerifierName = existing.VerifierName,
            RepositorySnapshotId = existing.RepositorySnapshotId,
            ValidFrom = existing.ValidFrom,
            ValidUntil = DateTime.UtcNow,
            CreatedAt = DateTime.UtcNow
        };
        revoked = ConstraintRecordContract.Seal(revoked);

        // Mark old record as superseded (by its revoked successor)
        SupersedeInternal(constraintId, revoked.Id, $"Revoked: {reason}");

        _records.Add(revoked);
        return revoked;
    }

    /// <summary>
    /// Returns all active constraints for the given requirement keys.
    /// Filters out superseded, revoked, and expired constraints.
    /// </summary>
    public IReadOnlyList<ConstraintRecord> GetActive(string? scopeFilter = null)
    {
        var now = DateTime.UtcNow;
        return _records
            .Where(r => r.Status == ConstraintStatus.Active &&
                        r.ValidUntil is null ||
                        r.ValidUntil is { } vu && vu > now)
            .Where(r => scopeFilter is null ||
                        string.IsNullOrEmpty(r.Scope) ||
                        r.Scope.Equals(scopeFilter, StringComparison.OrdinalIgnoreCase))
            .OrderBy(r => r.RequirementKey)
            .ThenByDescending(r => r.Revision)
            .ToList();
    }

    /// <summary>
    /// Returns the constraint with the given ID, or null if not found.
    /// </summary>
    public ConstraintRecord? GetById(Guid id) =>
        _records.FirstOrDefault(r => r.Id == id);

    /// <summary>
    /// Appends already-adjudicated history records (superseded/revoked trail)
    /// without re-running ingest authority rules. Seals are re-validated.
    /// </summary>
    public void AppendHistory(IEnumerable<ConstraintRecord> records)
    {
        ArgumentNullException.ThrowIfNull(records);
        foreach (var record in records)
        {
            var sealedRecord = ConstraintRecordContract.Seal(record);
            if (_records.Any(r => r.Id == sealedRecord.Id))
            {
                continue;
            }

            _records.Add(sealedRecord);
        }
    }

    /// <summary>
    /// Appends persisted conflicts without re-detection.
    /// </summary>
    public void AppendConflicts(IEnumerable<ConstraintConflict> conflicts)
    {
        ArgumentNullException.ThrowIfNull(conflicts);
        foreach (var conflict in conflicts)
        {
            if (_conflicts.Any(c => c.Id == conflict.Id))
            {
                continue;
            }

            _conflicts.Add(conflict);
        }
    }

    /// <summary>
    /// Returns all revisions of a constraint by its RequirementKey.
    /// </summary>
    public IReadOnlyList<ConstraintRecord> GetHistory(string requirementKey) =>
        _records
            .Where(r => r.RequirementKey == requirementKey)
            .OrderBy(r => r.Revision)
            .ToList();

    /// <summary>
    /// Detects conflicts among active constraints:
    /// - Same requirement key, same authority, different values
    /// - Contradictory scope patterns
    /// </summary>
    public IReadOnlyList<ConstraintConflict> DetectConflicts()
    {
        var active = GetActive();
        var newConflicts = new List<ConstraintConflict>();

        var byKey = active.GroupBy(r => r.RequirementKey, StringComparer.Ordinal);
        foreach (var group in byKey)
        {
            var records = group.ToList();
            if (records.Count <= 1)
                continue;

            // Multiple active records with same key but different content hashes
            var distinct = records
                .GroupBy(r => r.ContentHash, StringComparer.Ordinal)
                .ToList();
            if (distinct.Count > 1)
            {
                var first = distinct[0].First();
                var second = distinct[1].First();
                if (first.Authority == second.Authority)
                {
                    var conflict = new ConstraintConflict
                    {
                        ConstraintAId = first.Id,
                        ConstraintARevision = first.Revision,
                        ConstraintBId = second.Id,
                        ConstraintBRevision = second.Revision,
                        Reason = $"Active conflict in '{first.RequirementKey}': " +
                                 $"{distinct.Count} distinct versions with same authority ({first.Authority})"
                    };
                    newConflicts.Add(conflict);
                }
            }
        }

        _conflicts.AddRange(newConflicts);
        return newConflicts;
    }

    /// <summary>
    /// Creates a ConstraintSetRef capturing the current active constraint set.
    /// </summary>
    public ConstraintSetRef CreateSetRef()
    {
        var active = GetActive();
        return new ConstraintSetRef
        {
            Id = $"set-{Guid.NewGuid():N}",
            Revision = _records.Count > 0
                ? _records.Max(r => r.Revision)
                : 0,
            CanonicalSha256 = ConstraintLedgerFingerprint.CreateSetHash(active),
            ActiveCount = active.Count,
            CapturedAt = DateTime.UtcNow
        };
    }

    /// <summary>
    /// Validates that a TaskContract's constraints are consistent with the ledger.
    /// Returns violations if mandatory constraints are not satisfied.
    /// </summary>
    public IReadOnlyList<string> ValidateContract(TaskContract contract)
    {
        var violations = new List<string>();
        var active = GetActive();

        foreach (var constraint in active.Where(c =>
            c.Status == ConstraintStatus.Active &&
            c.Verifiability == ConstraintVerifiability.Deterministic &&
            c.VerifierName is not null))
        {
            // Deterministic constraints must be verifiable by a named verifier
            // This is a structural check — actual verification happens in the pipeline
            if (string.IsNullOrWhiteSpace(constraint.VerifierName))
            {
                violations.Add(
                    $"Constraint '{constraint.RequirementKey}' (rev {constraint.Revision}) " +
                    $"is deterministic but has no verifier assigned.");
            }
        }

        var unresolvedConflicts = _conflicts.Where(c => !c.Resolved).ToList();
        if (unresolvedConflicts.Count > 0)
        {
            violations.Add(
                $"{unresolvedConflicts.Count} unresolved constraint conflict(s) block approval.");
        }

        return violations;
    }

    private void SupersedeInternal(Guid oldId, Guid newId, string reason)
    {
        var old = _records.FirstOrDefault(r => r.Id == oldId);
        if (old is null || old.Status != ConstraintStatus.Active)
            return;

        var superseded = ConstraintRecordContract.Seal(new ConstraintRecord
        {
            SchemaVersion = old.SchemaVersion,
            Id = old.Id,
            RequirementKey = old.RequirementKey,
            Revision = old.Revision,
            Description = old.Description,
            Scope = old.Scope,
            Kind = old.Kind,
            Status = ConstraintStatus.Superseded,
            Authority = old.Authority,
            Origin = old.Origin,
            Verifiability = old.Verifiability,
            SupersedesId = old.SupersedesId,
            VerifierName = old.VerifierName,
            RepositorySnapshotId = old.RepositorySnapshotId,
            ValidFrom = old.ValidFrom,
            ValidUntil = DateTime.UtcNow,
            CreatedAt = old.CreatedAt
        });

        var index = _records.FindIndex(r => r.Id == oldId);
        if (index >= 0)
            _records[index] = superseded;
    }

    private void RecordConflict(ConstraintRecord a, ConstraintRecord b, string reason)
    {
        _conflicts.Add(new ConstraintConflict
        {
            ConstraintAId = a.Id,
            ConstraintARevision = a.Revision,
            ConstraintBId = b.Id,
            ConstraintBRevision = b.Revision,
            Reason = reason
        });
    }
}
