using System.Data;
using System.Security.Cryptography;
using System.Text.Json;
using AECS.Domain.Exceptions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Cryptography;
using AECS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using Npgsql;

namespace AECS.Infrastructure.Repositories;

public sealed partial class PostgreSqlExecutionEvidenceStore :
    IExecutionEvidenceStore,
    IEvidenceGraphSource,
    IHistoricalDecisionStore
{
    public const string ConnectionStringEnvironmentVariable =
        "AECS_POSTGRES_CONNECTION_STRING";

    private readonly DbContextOptions<AecsDbContext> _dbOptions;
    private readonly string? _keyDirectoryPath;
    private readonly Lazy<AuthenticatedEvidenceEnvelopeCodec> _envelopes;
    private readonly SemaphoreSlim _initializationGate = new(1, 1);
    private volatile bool _initialized;

    public PostgreSqlExecutionEvidenceStore(
        string connectionString,
        string? keyDirectoryPath = null)
    {
        _dbOptions = BuildOptions(connectionString);
        _keyDirectoryPath = Path.GetFullPath(
            keyDirectoryPath ?? JsonExecutionEvidenceStore.GetDefaultKeyDirectoryPath());
        _envelopes = new Lazy<AuthenticatedEvidenceEnvelopeCodec>(
            () => new AuthenticatedEvidenceEnvelopeCodec(
                RsaEvidenceSignatureService.LoadOrCreate(_keyDirectoryPath)),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public PostgreSqlExecutionEvidenceStore(
        string connectionString,
        IEvidenceSignatureService signatureService)
    {
        ArgumentNullException.ThrowIfNull(signatureService);
        _dbOptions = BuildOptions(connectionString);
        _envelopes = new Lazy<AuthenticatedEvidenceEnvelopeCodec>(
            () => new AuthenticatedEvidenceEnvelopeCodec(signatureService));
    }

    public static string GetRequiredConnectionString()
    {
        var connectionString = Environment.GetEnvironmentVariable(
            ConnectionStringEnvironmentVariable);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"PostgreSQL evidence storage requires the secret environment variable " +
                $"{ConnectionStringEnvironmentVariable}.");
        }

        return connectionString;
    }

    public void EnsureRepositoryIsolation(string repositoryPath)
    {
        if (_keyDirectoryPath is null)
            return;

        var repositoryRoot = Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var keyDirectory = _keyDirectoryPath
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repositoryPrefix = repositoryRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(keyDirectory, repositoryRoot, comparison) ||
            keyDirectory.StartsWith(repositoryPrefix, comparison))
        {
            throw new InvalidOperationException(
                $"Evidence key directory '{keyDirectory}' must be outside target repository " +
                $"'{repositoryRoot}'.");
        }
    }

    public async Task<string> SaveAsync(
        ExecutionEvidence evidence,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(evidence);
        AuthenticatedEvidenceEnvelopeCodec.ValidateEvidenceStructure(evidence);
        EnsureRepositoryIsolation(evidence.Baseline.RepositoryPath);
        var envelope = _envelopes.Value.Create(evidence);
        var contentHash = AuthenticatedEvidenceEnvelopeCodec.ContentHash(evidence);

        await EnsureInitializedAsync(cancellationToken);
        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockEvidenceIdentityAsync(
            db,
            transaction,
            evidence.Id,
            cancellationToken);

        var existing = await db.ExecutionEvidenceRecords
            .AsNoTracking()
            .Include(record => record.PromotionEvents)
            .Include(record => record.ReplayEvents)
            .SingleOrDefaultAsync(record => record.Id == evidence.Id, cancellationToken);
        if (existing is not null)
        {
            ValidateStoredRecord(existing, evidence.Id);
            EnsureIdempotentExecution(existing, contentHash);
            await transaction.CommitAsync(cancellationToken);
            return Location(evidence.Id);
        }

        db.ExecutionEvidenceRecords.Add(ToRecord(envelope, contentHash));
        await db.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return Location(evidence.Id);
    }

    public async Task<ExecutionEvidence?> LoadAsync(
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        await using var db = CreateDbContext();
        var record = await db.ExecutionEvidenceRecords
            .AsNoTracking()
            .Include(item => item.PromotionEvents)
            .Include(item => item.ReplayEvents)
            .SingleOrDefaultAsync(item => item.Id == evidenceId, cancellationToken);
        if (record is null)
            return null;

        var envelope = ValidateStoredRecord(record, evidenceId);
        foreach (var promotionEvent in envelope.PromotionEvents)
            envelope.Evidence.Promotions.Add(promotionEvent.Promotion);
        return envelope.Evidence;
    }

    public async Task AppendPromotionAsync(
        Guid evidenceId,
        CandidatePromotionEvidence promotion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        await EnsureInitializedAsync(cancellationToken);

        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockEvidenceIdentityAsync(
            db,
            transaction,
            evidenceId,
            cancellationToken);
        if (!await LockEvidenceAsync(db, transaction, evidenceId, cancellationToken))
            throw new FileNotFoundException("Execution evidence was not found in PostgreSQL.");

        var record = await db.ExecutionEvidenceRecords
            .AsNoTracking()
            .Include(item => item.PromotionEvents)
            .Include(item => item.ReplayEvents)
            .SingleAsync(item => item.Id == evidenceId, cancellationToken);
        var envelope = ValidateStoredRecord(record, evidenceId);

        var existingEvent = envelope.PromotionEvents
            .FirstOrDefault(item => item.Promotion.Id == promotion.Id);
        if (existingEvent is not null)
        {
            if (!AuthenticatedEvidenceEnvelopeCodec.PromotionEquals(
                    existingEvent.Promotion,
                    promotion))
            {
                throw new InvalidOperationException(
                    "Promotion evidence ID is already associated with different content.");
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        _envelopes.Value.AppendPromotion(envelope, promotion);
        var appended = envelope.PromotionEvents[^1];
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO execution_evidence_promotion_events
                ("Id", "ExecutionEvidenceId", "Sequence", "PreviousSignature",
                 "PromotionJson", "SealJson", "SignedAt")
            VALUES
                ({appended.Promotion.Id}, {evidenceId}, {appended.Sequence},
                 {appended.PreviousSignature},
                 CAST({Serialize(appended.Promotion)} AS jsonb),
                 CAST({Serialize(appended.Seal)} AS jsonb),
                 {appended.Seal.SignedAt})
            """, cancellationToken);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE execution_evidence
            SET "EventCount" = {envelope.PromotionEvents.Count + envelope.ReplayEvents.Count},
                "ChainSealJson" = CAST({Serialize(envelope.ChainSeal)} AS jsonb),
                "UpdatedAt" = {envelope.ChainSeal.SignedAt}
            WHERE "Id" = {evidenceId}
            """, cancellationToken);
        if (inserted != 1 || updated != 1)
        {
            throw new DBConcurrencyException(
                "PostgreSQL promotion append did not affect the expected aggregate rows.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task AppendReplayAsync(
        Guid evidenceId,
        ExecutionReplayEvidence replay,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(replay);
        await EnsureInitializedAsync(cancellationToken);

        await using var db = CreateDbContext();
        await using var transaction = await db.Database.BeginTransactionAsync(
            IsolationLevel.ReadCommitted,
            cancellationToken);
        await LockEvidenceIdentityAsync(db, transaction, evidenceId, cancellationToken);
        if (!await LockEvidenceAsync(db, transaction, evidenceId, cancellationToken))
            throw new FileNotFoundException("Execution evidence was not found in PostgreSQL.");

        var record = await db.ExecutionEvidenceRecords
            .AsNoTracking()
            .Include(item => item.PromotionEvents)
            .Include(item => item.ReplayEvents)
            .SingleAsync(item => item.Id == evidenceId, cancellationToken);
        var envelope = ValidateStoredRecord(record, evidenceId);

        var existingEvent = envelope.ReplayEvents
            .FirstOrDefault(item => item.Replay.Id == replay.Id);
        if (existingEvent is not null)
        {
            if (!AuthenticatedEvidenceEnvelopeCodec.ReplayEquals(
                    existingEvent.Replay,
                    replay))
            {
                throw new InvalidOperationException(
                    "Replay evidence ID is already associated with different content.");
            }

            await transaction.CommitAsync(cancellationToken);
            return;
        }

        _envelopes.Value.AppendReplay(envelope, replay);
        var appended = envelope.ReplayEvents[^1];
        var inserted = await db.Database.ExecuteSqlInterpolatedAsync($"""
            INSERT INTO execution_evidence_replay_events
                ("Id", "ExecutionEvidenceId", "Sequence", "PreviousSignature",
                 "ReplayJson", "SealJson", "SignedAt")
            VALUES
                ({appended.Replay.Id}, {evidenceId}, {appended.Sequence},
                 {appended.PreviousSignature},
                 CAST({Serialize(appended.Replay)} AS jsonb),
                 CAST({Serialize(appended.Seal)} AS jsonb),
                 {appended.Seal.SignedAt})
            """, cancellationToken);
        var updated = await db.Database.ExecuteSqlInterpolatedAsync($"""
            UPDATE execution_evidence
            SET "EventCount" = {envelope.PromotionEvents.Count + envelope.ReplayEvents.Count},
                "ChainSealJson" = CAST({Serialize(envelope.ChainSeal)} AS jsonb),
                "UpdatedAt" = {envelope.ChainSeal.SignedAt}
            WHERE "Id" = {evidenceId}
            """, cancellationToken);
        if (inserted != 1 || updated != 1)
        {
            throw new DBConcurrencyException(
                "PostgreSQL replay append did not affect the expected aggregate rows.");
        }

        await transaction.CommitAsync(cancellationToken);
    }

    public async Task<EvidenceGraphQueryResult> QueryEvidenceGraphsAsync(
        EvidenceGraphQuery query,
        EvidenceReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(scope);
        EnsureRepositoryIsolation(scope.RepositoryPath);
        await EnsureInitializedAsync(cancellationToken);

        await using var db = CreateDbContext();
        var records = db.ExecutionEvidenceRecords
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.PromotionEvents)
            .Include(item => item.ReplayEvents)
            .AsQueryable();
        if (query.TaskId is not null)
            records = records.Where(item => item.TaskId == query.TaskId);
        if (query.RunId is not null)
            records = records.Where(item => item.AgentRunId == query.RunId.Value);
        if (query.CandidateId is not null)
            records = records.Where(item => item.CandidateId == query.CandidateId.Value);

        var storedRecords = await records.ToListAsync(cancellationToken);
        var graphs = new List<EvidenceGraph>();
        var diagnostics = new List<string>();
        foreach (var record in storedRecords)
        {
            try
            {
                var envelope = ValidateStoredRecord(record, record.Id);
                EvidenceGraphProjection.EnsureAuthorized(
                    envelope.Evidence.Baseline.RepositoryPath,
                    scope);
                var graph = EvidenceGraphProjection.Project(envelope, scope);
                if (EvidenceGraphProjection.Matches(graph.Summary, query))
                    graphs.Add(graph);
            }
            catch (UnauthorizedAccessException)
            {
                // Repository-scoped reads intentionally reveal no cross-repository record.
            }
            catch (EvidenceIntegrityException)
            {
                diagnostics.Add("A PostgreSQL evidence record was invalid and was omitted.");
            }
        }

        return new EvidenceGraphQueryResult
        {
            Items = graphs
                .OrderByDescending(item => item.Summary.CreatedAt)
                .Take(query.Limit)
                .Select(item => item.Summary)
                .ToList(),
            Diagnostics = diagnostics.Distinct(StringComparer.Ordinal).ToList()
        };
    }

    public async Task<EvidenceGraph?> LoadEvidenceGraphAsync(
        Guid evidenceId,
        EvidenceReadScope scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        EnsureRepositoryIsolation(scope.RepositoryPath);
        await EnsureInitializedAsync(cancellationToken);
        await using var db = CreateDbContext();
        var record = await db.ExecutionEvidenceRecords
            .AsNoTracking()
            .AsSplitQuery()
            .Include(item => item.PromotionEvents)
            .Include(item => item.ReplayEvents)
            .SingleOrDefaultAsync(item => item.Id == evidenceId, cancellationToken);
        if (record is null)
            return null;

        var envelope = ValidateStoredRecord(record, evidenceId);
        EvidenceGraphProjection.EnsureAuthorized(
            envelope.Evidence.Baseline.RepositoryPath,
            scope);
        return EvidenceGraphProjection.Project(envelope, scope);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationGate.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            await using var db = CreateDbContext();
            await db.Database.MigrateAsync(cancellationToken);
            _initialized = true;
        }
        finally
        {
            _initializationGate.Release();
        }
    }

    private SignedExecutionEvidenceEnvelope ValidateStoredRecord(
        ExecutionEvidenceRecord record,
        Guid expectedEvidenceId)
    {
        try
        {
            var evidence = Deserialize<ExecutionEvidence>(record.EvidenceJson);
            if (evidence.Baseline is null)
                throw new EvidenceIntegrityException("PostgreSQL evidence is incomplete.");
            EnsureRepositoryIsolation(evidence.Baseline.RepositoryPath);

            var envelope = new SignedExecutionEvidenceEnvelope
            {
                SchemaVersion = record.SchemaVersion,
                Evidence = evidence,
                Seal = Deserialize<EvidenceSeal>(record.EvidenceSealJson),
                ChainSeal = Deserialize<EvidenceSeal>(record.ChainSealJson),
                PromotionEvents = record.PromotionEvents
                    .OrderBy(item => item.Sequence)
                    .Select(item => new SignedPromotionEvent
                    {
                        Sequence = item.Sequence,
                        PreviousSignature = item.PreviousSignature,
                        Promotion = Deserialize<CandidatePromotionEvidence>(item.PromotionJson),
                        Seal = Deserialize<EvidenceSeal>(item.SealJson)
                    })
                    .ToList(),
                ReplayEvents = record.ReplayEvents
                    .OrderBy(item => item.Sequence)
                    .Select(item => new SignedReplayEvent
                    {
                        Sequence = item.Sequence,
                        PreviousSignature = item.PreviousSignature,
                        Replay = Deserialize<ExecutionReplayEvidence>(item.ReplayJson),
                        Seal = Deserialize<EvidenceSeal>(item.SealJson)
                    })
                    .ToList()
            };

            ValidateRelationalProjection(record, envelope, expectedEvidenceId);
            _envelopes.Value.Validate(envelope, expectedEvidenceId);
            return envelope;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (EvidenceIntegrityException)
        {
            throw;
        }
        catch (Exception ex) when (ex is JsonException or CryptographicException or FormatException)
        {
            throw new EvidenceIntegrityException(
                "PostgreSQL evidence is malformed or cannot be cryptographically verified.",
                ex);
        }
    }

    private static void ValidateRelationalProjection(
        ExecutionEvidenceRecord record,
        SignedExecutionEvidenceEnvelope envelope,
        Guid expectedEvidenceId)
    {
        var evidence = envelope.Evidence;
        if (record.Id != expectedEvidenceId || evidence.Id != record.Id)
            throw new EvidenceIntegrityException("PostgreSQL evidence ID projection is invalid.");
        if (!string.Equals(record.TaskId, evidence.TaskContract.Id, StringComparison.Ordinal))
            throw new EvidenceIntegrityException("PostgreSQL task ID projection is invalid.");
        if (record.AgentRunId != evidence.AgentRun.Id)
            throw new EvidenceIntegrityException("PostgreSQL agent run projection is invalid.");
        if (record.CandidateId != evidence.CandidateChangeSet.Id)
            throw new EvidenceIntegrityException("PostgreSQL candidate projection is invalid.");
        if (record.EventCount != envelope.PromotionEvents.Count + envelope.ReplayEvents.Count)
            throw new EvidenceIntegrityException("PostgreSQL event count projection is invalid.");

        var contentHash = AuthenticatedEvidenceEnvelopeCodec.ContentHash(evidence);
        if (!FixedTimeEquals(record.EvidenceContentHash, contentHash))
            throw new EvidenceIntegrityException("PostgreSQL evidence content hash is invalid.");

        for (var index = 0; index < record.PromotionEvents.Count; index++)
        {
            var relational = record.PromotionEvents.OrderBy(item => item.Sequence).ElementAt(index);
            var signed = envelope.PromotionEvents[index];
            if (relational.Id != signed.Promotion.Id ||
                relational.ExecutionEvidenceId != record.Id ||
                relational.Sequence != signed.Sequence ||
                Math.Abs((relational.SignedAt - signed.Seal.SignedAt).Ticks) >=
                    TimeSpan.TicksPerMicrosecond)
            {
                throw new EvidenceIntegrityException(
                    "PostgreSQL promotion event projection is invalid.");
            }
        }

        for (var index = 0; index < record.ReplayEvents.Count; index++)
        {
            var relational = record.ReplayEvents.OrderBy(item => item.Sequence).ElementAt(index);
            var signed = envelope.ReplayEvents[index];
            if (relational.Id != signed.Replay.Id ||
                relational.ExecutionEvidenceId != record.Id ||
                relational.Sequence != signed.Sequence ||
                Math.Abs((relational.SignedAt - signed.Seal.SignedAt).Ticks) >=
                    TimeSpan.TicksPerMicrosecond)
            {
                throw new EvidenceIntegrityException(
                    "PostgreSQL replay event projection is invalid.");
            }
        }
    }

    private static void EnsureIdempotentExecution(
        ExecutionEvidenceRecord existing,
        string contentHash)
    {
        if (!FixedTimeEquals(existing.EvidenceContentHash, contentHash))
        {
            throw new InvalidOperationException(
                "Execution evidence ID is already associated with different content.");
        }
    }

    private static ExecutionEvidenceRecord ToRecord(
        SignedExecutionEvidenceEnvelope envelope,
        string contentHash) => new()
        {
            Id = envelope.Evidence.Id,
            TaskId = envelope.Evidence.TaskContract.Id,
            AgentRunId = envelope.Evidence.AgentRun.Id,
            CandidateId = envelope.Evidence.CandidateChangeSet.Id,
            SchemaVersion = envelope.SchemaVersion,
            EvidenceContentHash = contentHash,
            EvidenceJson = Serialize(envelope.Evidence),
            EvidenceSealJson = Serialize(envelope.Seal),
            ChainSealJson = Serialize(envelope.ChainSeal),
            EventCount = 0,
            CreatedAt = envelope.Seal.SignedAt,
            UpdatedAt = envelope.ChainSeal.SignedAt
        };

    private static async Task<bool> LockEvidenceAsync(
        AecsDbContext db,
        IDbContextTransaction transaction,
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            "SELECT \"Id\" FROM execution_evidence WHERE \"Id\" = @evidenceId FOR UPDATE";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "evidenceId";
        parameter.DbType = DbType.Guid;
        parameter.Value = evidenceId;
        command.Parameters.Add(parameter);
        return await command.ExecuteScalarAsync(cancellationToken) is not null;
    }

    private static async Task LockEvidenceIdentityAsync(
        AecsDbContext db,
        IDbContextTransaction transaction,
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        var connection = db.Database.GetDbConnection();
        await using var command = connection.CreateCommand();
        command.Transaction = transaction.GetDbTransaction();
        command.CommandText =
            "SELECT pg_advisory_xact_lock(hashtextextended(@evidenceId, 0))";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "evidenceId";
        parameter.DbType = DbType.String;
        parameter.Value = evidenceId.ToString("N");
        command.Parameters.Add(parameter);
        await command.ExecuteScalarAsync(cancellationToken);
    }

    private AecsDbContext CreateDbContext() => new(_dbOptions);

    private static DbContextOptions<AecsDbContext> BuildOptions(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            throw new InvalidOperationException("PostgreSQL evidence connection string is required.");

        string sanitizedConnectionString;
        try
        {
            sanitizedConnectionString = new NpgsqlConnectionStringBuilder(connectionString)
                .ConnectionString;
        }
        catch (ArgumentException ex)
        {
            throw new InvalidOperationException(
                "PostgreSQL evidence connection configuration is invalid.",
                ex);
        }

        return new DbContextOptionsBuilder<AecsDbContext>()
            .UseNpgsql(sanitizedConnectionString)
            .Options;
    }

    private static string Serialize<T>(T value) => JsonSerializer.Serialize(
        value,
        EvidenceEnvelopeFormat.SerializerOptions);

    private static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, EvidenceEnvelopeFormat.SerializerOptions)
        ?? throw new EvidenceIntegrityException(
            $"PostgreSQL evidence component '{typeof(T).Name}' is empty.");

    private static bool FixedTimeEquals(string left, string right)
    {
        var leftBytes = System.Text.Encoding.UTF8.GetBytes(left ?? string.Empty);
        var rightBytes = System.Text.Encoding.UTF8.GetBytes(right ?? string.Empty);
        return leftBytes.Length == rightBytes.Length &&
            CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private static string Location(Guid evidenceId) =>
        $"postgresql://aecs/execution-evidence/{evidenceId:N}";
}
