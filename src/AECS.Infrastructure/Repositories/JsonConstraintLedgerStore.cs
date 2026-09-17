using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Repositories;

/// <summary>
/// File-backed constraint ledger store, scoped per repository under the
/// evidence root. Load validates schema, content seals, and origin allowlist;
/// records that cannot be trusted are rejected (plan §4.1, case R4).
/// </summary>
public sealed class JsonConstraintLedgerStore : IConstraintLedgerStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private readonly string _rootPath;

    public JsonConstraintLedgerStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
    }

    public async Task<ConstraintLedgerSnapshot?> LoadAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var path = SnapshotPath(repositoryPath);
        if (!File.Exists(path))
        {
            return null;
        }

        var json = await File.ReadAllTextAsync(path, cancellationToken);
        var snapshot = JsonSerializer.Deserialize<ConstraintLedgerSnapshot>(json, JsonOptions)
            ?? throw new InvalidOperationException(
                "Constraint ledger store file is unreadable or corrupted.");

        if (snapshot.SchemaVersion != ConstraintLedgerSchema.StoreVersion ||
            snapshot.Records is null ||
            snapshot.Conflicts is null ||
            !string.Equals(snapshot.RepositoryKey, RepositoryKey(repositoryPath), StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Constraint ledger store snapshot is incomplete, inconsistent, " +
                "or has an unsupported schema.");
        }

        foreach (var record in snapshot.Records)
        {
            ConstraintRecordContract.Validate(record);
            if (!ConstraintLedgerPersistence.IsPersistable(record.Origin))
            {
                throw new InvalidOperationException(
                    $"Constraint '{record.RequirementKey}' has untrusted origin " +
                    $"'{record.Origin}' for durable persistence.");
            }
        }

        foreach (var conflict in snapshot.Conflicts)
        {
            if (conflict.ConstraintAId == Guid.Empty ||
                conflict.ConstraintBId == Guid.Empty ||
                string.IsNullOrWhiteSpace(conflict.Reason))
            {
                throw new InvalidOperationException(
                    "Constraint ledger store contains an incomplete conflict record.");
            }
        }

        return snapshot;
    }

    public async Task SaveAsync(
        string repositoryPath,
        ConstraintLedgerSnapshot snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        var persistableRecords = snapshot.Records
            .Where(record => ConstraintLedgerPersistence.IsPersistable(record.Origin))
            .ToList();
        foreach (var record in persistableRecords)
        {
            ConstraintRecordContract.Validate(record);
        }

        var toStore = new ConstraintLedgerSnapshot
        {
            RepositoryKey = RepositoryKey(repositoryPath),
            Records = persistableRecords,
            Conflicts = snapshot.Conflicts.ToList(),
            SavedAt = DateTime.UtcNow
        };

        var path = SnapshotPath(repositoryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(toStore, JsonOptions);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    private string SnapshotPath(string repositoryPath) => Path.Combine(
        _rootPath,
        "constraint-ledger",
        RepositoryKey(repositoryPath) + ".json");

    private static string RepositoryKey(string repositoryPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        var normalized = Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Replace('\\', '/')
            .ToLowerInvariant();
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized))).ToLowerInvariant();
    }
}
