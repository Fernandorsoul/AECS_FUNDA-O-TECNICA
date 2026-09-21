using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Repositories;

/// <summary>
/// File-backed constraint ledger store, scoped per repository under the
/// evidence root. Every file is an authenticated envelope: HMAC-SHA256 over the
/// canonical snapshot using a 256-bit key kept in the evidence key directory
/// (created on first save). Load fails closed on missing key, missing MAC, or
/// MAC mismatch before any record is trusted; content seals and the origin
/// allowlist are then re-validated (plan §4.1, case R4).
/// </summary>
public sealed class JsonConstraintLedgerStore : IConstraintLedgerStore
{
    public const string KeyFileName = "constraint-ledger.hmac.key";
    public const string FileSchemaVersion = "aecs.constraint-ledger-store-file/v1";
    public const string MacPrefix = "hmac-sha256:";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private static readonly JsonSerializerOptions MacPayloadOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    private readonly string _rootPath;
    private readonly string _keyDirectory;

    public JsonConstraintLedgerStore(string rootPath, string? keyDirectory = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = rootPath;
        _keyDirectory = string.IsNullOrWhiteSpace(keyDirectory)
            ? Path.Combine(rootPath, "keys")
            : keyDirectory;
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
        var file = JsonSerializer.Deserialize<StoreFile>(json, JsonOptions);
        if (file is null ||
            file.SchemaVersion != FileSchemaVersion ||
            file.Snapshot is null ||
            string.IsNullOrWhiteSpace(file.Mac))
        {
            throw new InvalidOperationException(
                "Constraint ledger store file is unreadable, unauthenticated, or corrupted.");
        }

        var key = LoadKey();
        var expectedMac = ComputeMac(file.Snapshot, key);
        if (!CryptographicOperations.FixedTimeEquals(
                Encoding.UTF8.GetBytes(expectedMac),
                Encoding.UTF8.GetBytes(file.Mac)))
        {
            throw new InvalidOperationException(
                "Constraint ledger store MAC is invalid; the file was modified " +
                "or was written without the store key.");
        }

        var snapshot = file.Snapshot;
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

        var key = LoadOrCreateKey();
        var envelope = new StoreFile
        {
            SchemaVersion = FileSchemaVersion,
            Snapshot = toStore,
            Mac = ComputeMac(toStore, key)
        };

        var path = SnapshotPath(repositoryPath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var json = JsonSerializer.Serialize(envelope, JsonOptions);
        var tempPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        await File.WriteAllTextAsync(tempPath, json, cancellationToken);
        File.Move(tempPath, path, overwrite: true);
    }

    /// <summary>
    /// Canonical HMAC for a snapshot. Public so tests can build authenticated
    /// fixtures (e.g. origin-allowlist cases) without duplicating payload options.
    /// </summary>
    public static string ComputeMac(ConstraintLedgerSnapshot snapshot, byte[] key)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(key);
        if (key.Length < 32)
        {
            throw new InvalidOperationException("Constraint ledger HMAC key is too short.");
        }

        var payload = JsonSerializer.Serialize(snapshot, MacPayloadOptions);
        var mac = HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(payload));
        return MacPrefix + Convert.ToHexString(mac).ToLowerInvariant();
    }

    private byte[] LoadOrCreateKey()
    {
        Directory.CreateDirectory(_keyDirectory);
        var path = Path.Combine(_keyDirectory, KeyFileName);
        if (File.Exists(path))
        {
            return ReadKey(path);
        }

        var bytes = RandomNumberGenerator.GetBytes(32);
        var hex = Convert.ToHexString(bytes).ToLowerInvariant();
        try
        {
            using var stream = new FileStream(
                path,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None);
            using var writer = new StreamWriter(stream);
            writer.Write(hex);
        }
        catch (IOException)
        {
            // Lost a create race — the winner's key is authoritative.
            return ReadKey(path);
        }

        return bytes;
    }

    private byte[] LoadKey()
    {
        var path = Path.Combine(_keyDirectory, KeyFileName);
        if (!File.Exists(path))
        {
            throw new InvalidOperationException(
                "Constraint ledger HMAC key is unavailable; refusing to load an " +
                "unauthenticated constraint ledger store.");
        }

        return ReadKey(path);
    }

    private static byte[] ReadKey(string path)
    {
        var hex = File.ReadAllText(path).Trim();
        if (hex.Length != 64 || !hex.All(Uri.IsHexDigit))
        {
            throw new InvalidOperationException(
                "Constraint ledger HMAC key is malformed.");
        }

        return Convert.FromHexString(hex);
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

    private sealed class StoreFile
    {
        public string SchemaVersion { get; init; } = string.Empty;
        public ConstraintLedgerSnapshot? Snapshot { get; init; }
        public string Mac { get; init; } = string.Empty;
    }
}
