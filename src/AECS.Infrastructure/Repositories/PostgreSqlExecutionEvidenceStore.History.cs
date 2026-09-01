using System.Data;
using System.Text.Json;
using AECS.Domain.Models;
using AECS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AECS.Infrastructure.Repositories;

public sealed partial class PostgreSqlExecutionEvidenceStore
{
    public async Task<string> SaveHistoricalDecisionAsync(
        HistoricalDecision decision,
        CancellationToken cancellationToken)
    {
        var sealedDecision = HistoricalDecisionContract.Seal(decision);
        await EnsureInitializedAsync(cancellationToken);
        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockHistoricalRecordAsync(
            db,
            $"decision:{sealedDecision.Id}:{sealedDecision.Version}",
            cancellationToken);
        var existing = await db.HistoricalDecisionRecords.AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.Id == sealedDecision.Id &&
                record.Version == sealedDecision.Version,
                cancellationToken);
        if (existing is not null)
        {
            var loaded = FromRecord(existing);
            if (!string.Equals(
                    loaded.ContentHash,
                    sealedDecision.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Historical decision ID and version already contain different content.");
            }
            await transaction.CommitAsync(cancellationToken);
            return DecisionLocation(sealedDecision.Id, sealedDecision.Version);
        }

        db.HistoricalDecisionRecords.Add(ToRecord(sealedDecision));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return DecisionLocation(sealedDecision.Id, sealedDecision.Version);
    }

    public async Task<IReadOnlyList<HistoricalDecision>> LoadHistoricalDecisionsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var db = CreateDbContext();
        var records = await db.HistoricalDecisionRecords.AsNoTracking()
            .OrderBy(record => record.Id)
            .ThenBy(record => record.Version)
            .ToListAsync(cancellationToken);
        return records.Select(FromRecord).ToList();
    }

    public async Task<string> SaveHistoricalDecisionSuppressionAsync(
        HistoricalDecisionSuppression suppression,
        CancellationToken cancellationToken)
    {
        var sealedSuppression = HistoricalDecisionContract.Seal(suppression);
        await EnsureInitializedAsync(cancellationToken);
        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockHistoricalRecordAsync(
            db,
            $"suppression:{sealedSuppression.Id}:{sealedSuppression.Version}",
            cancellationToken);
        var decisionExists = await db.HistoricalDecisionRecords.AnyAsync(record =>
            record.Id == sealedSuppression.DecisionId &&
            record.Version == sealedSuppression.DecisionVersion,
            cancellationToken);
        if (!decisionExists)
        {
            throw new InvalidOperationException(
                "Historical decision suppression references a decision version that is absent.");
        }
        var existing = await db.HistoricalDecisionSuppressionRecords.AsNoTracking()
            .SingleOrDefaultAsync(record =>
                record.Id == sealedSuppression.Id &&
                record.Version == sealedSuppression.Version,
                cancellationToken);
        if (existing is not null)
        {
            var loaded = FromRecord(existing);
            if (!string.Equals(
                    loaded.ContentHash,
                    sealedSuppression.ContentHash,
                    StringComparison.Ordinal))
            {
                throw new InvalidOperationException(
                    "Historical suppression ID and version already contain different content.");
            }
            await transaction.CommitAsync(cancellationToken);
            return SuppressionLocation(sealedSuppression.Id, sealedSuppression.Version);
        }

        db.HistoricalDecisionSuppressionRecords.Add(ToRecord(sealedSuppression));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return SuppressionLocation(sealedSuppression.Id, sealedSuppression.Version);
    }

    public async Task<IReadOnlyList<HistoricalDecisionSuppression>>
        LoadHistoricalDecisionSuppressionsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var db = CreateDbContext();
        var records = await db.HistoricalDecisionSuppressionRecords.AsNoTracking()
            .OrderBy(record => record.Id)
            .ThenBy(record => record.Version)
            .ToListAsync(cancellationToken);
        return records.Select(FromRecord).ToList();
    }

    private static HistoricalDecisionStorageRecord ToRecord(HistoricalDecision decision) =>
        new()
        {
            Id = decision.Id,
            Version = decision.Version,
            SchemaVersion = decision.SchemaVersion,
            Source = decision.Source,
            Authority = decision.Authority,
            ReviewStatus = (int)decision.Review.Status,
            Enforcement = (int)decision.Enforcement,
            ValidFrom = decision.ValidFrom,
            ValidUntil = decision.ValidUntil,
            ContentHash = decision.ContentHash,
            DecisionJson = JsonSerializer.Serialize(
                decision,
                EvidenceEnvelopeFormat.SerializerOptions),
            CreatedAt = decision.CreatedAt
        };

    private static HistoricalDecisionSuppressionStorageRecord ToRecord(
        HistoricalDecisionSuppression suppression) => new()
        {
            Id = suppression.Id,
            Version = suppression.Version,
            SchemaVersion = suppression.SchemaVersion,
            DecisionId = suppression.DecisionId,
            DecisionVersion = suppression.DecisionVersion,
            Actor = suppression.Actor,
            ExpiresAt = suppression.ExpiresAt,
            ContentHash = suppression.ContentHash,
            SuppressionJson = JsonSerializer.Serialize(
                suppression,
                EvidenceEnvelopeFormat.SerializerOptions),
            CreatedAt = suppression.CreatedAt
        };

    private static HistoricalDecision FromRecord(HistoricalDecisionStorageRecord record)
    {
        var decision = JsonSerializer.Deserialize<HistoricalDecision>(
                record.DecisionJson,
                EvidenceEnvelopeFormat.SerializerOptions) ??
            throw new InvalidOperationException("PostgreSQL historical decision is empty.");
        HistoricalDecisionContract.Validate(decision);
        if (!decision.Id.Equals(record.Id, StringComparison.Ordinal) ||
            decision.Version != record.Version ||
            !decision.SchemaVersion.Equals(record.SchemaVersion, StringComparison.Ordinal) ||
            !decision.ContentHash.Equals(record.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PostgreSQL historical decision projections are inconsistent.");
        }
        return decision;
    }

    private static HistoricalDecisionSuppression FromRecord(
        HistoricalDecisionSuppressionStorageRecord record)
    {
        var suppression = JsonSerializer.Deserialize<HistoricalDecisionSuppression>(
                record.SuppressionJson,
                EvidenceEnvelopeFormat.SerializerOptions) ??
            throw new InvalidOperationException("PostgreSQL historical suppression is empty.");
        HistoricalDecisionContract.Validate(suppression);
        if (!suppression.Id.Equals(record.Id, StringComparison.Ordinal) ||
            suppression.Version != record.Version ||
            !suppression.SchemaVersion.Equals(record.SchemaVersion, StringComparison.Ordinal) ||
            !suppression.ContentHash.Equals(record.ContentHash, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "PostgreSQL historical suppression projections are inconsistent.");
        }
        return suppression;
    }

    private static Task<int> LockHistoricalRecordAsync(
        AecsDbContext db,
        string key,
        CancellationToken cancellationToken) => db.Database.ExecuteSqlInterpolatedAsync(
        $"SELECT pg_advisory_xact_lock(hashtextextended({key}, 0))",
        cancellationToken);

    private static string DecisionLocation(string id, int version) =>
        $"postgresql://aecs/historical-decisions/" +
        $"{Uri.EscapeDataString(id)}/versions/{version}";

    private static string SuppressionLocation(string id, int version) =>
        $"postgresql://aecs/historical-decision-suppressions/" +
        $"{Uri.EscapeDataString(id)}/versions/{version}";
}
