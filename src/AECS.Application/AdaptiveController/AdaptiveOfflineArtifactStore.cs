using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AECS.Application.AdaptiveController;

public sealed class AdaptiveOfflineArtifactStore
{
    private readonly string _sessionPath;
    private readonly string _pairsDirectory;

    public AdaptiveOfflineArtifactStore(string outputDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        OutputDirectory = Path.GetFullPath(outputDirectory);
        _sessionPath = Path.Combine(OutputDirectory, "session.json");
        _pairsDirectory = Path.Combine(OutputDirectory, "pairs");
        ReportPath = Path.Combine(OutputDirectory, "report.json");
    }

    public string OutputDirectory { get; }
    public string ReportPath { get; }

    public async Task InitializeAsync(
        string datasetHash,
        bool resume,
        CancellationToken cancellationToken)
    {
        ValidateHash(datasetHash);
        if (Directory.Exists(OutputDirectory))
        {
            if (!resume)
            {
                throw new InvalidOperationException(
                    "Adaptive offline output already exists; pass resume explicitly.");
            }
            if (!File.Exists(_sessionPath))
            {
                throw new InvalidOperationException(
                    "Adaptive offline output has no authenticated session metadata.");
            }
            var session = await ReadAsync<AdaptiveOfflineSession>(
                _sessionPath,
                cancellationToken);
            if (session.SchemaVersion != AdaptiveOfflineSchema.SessionVersion ||
                session.DatasetHash != datasetHash)
            {
                throw new InvalidOperationException(
                    "Adaptive offline resume dataset does not match the existing session.");
            }
            Directory.CreateDirectory(_pairsDirectory);
            return;
        }

        Directory.CreateDirectory(_pairsDirectory);
        await WriteNewAsync(
            _sessionPath,
            new AdaptiveOfflineSession
            {
                DatasetHash = datasetHash,
                StartedAtUtc = DateTime.UtcNow
            },
            cancellationToken);
    }

    public async Task<AdaptiveOfflinePairResult?> LoadAsync(
        string pairKey,
        string datasetHash,
        CancellationToken cancellationToken)
    {
        ValidatePairKey(pairKey);
        ValidateHash(datasetHash);
        var path = PairPath(pairKey);
        if (!File.Exists(path))
            return null;
        var checkpoint = await ReadAsync<AdaptiveOfflineCheckpoint>(path, cancellationToken);
        if (checkpoint.SchemaVersion != AdaptiveOfflineSchema.CheckpointVersion ||
            checkpoint.DatasetHash != datasetHash ||
            checkpoint.PairKey != pairKey ||
            checkpoint.Result.PairKey != pairKey ||
            checkpoint.Result.DatasetHash != datasetHash ||
            checkpoint.Fingerprint != Fingerprint(checkpoint.Result))
        {
            throw new InvalidOperationException(
                $"Adaptive offline checkpoint '{pairKey}' failed integrity validation.");
        }
        return checkpoint.Result;
    }

    public Task SaveAsync(
        AdaptiveOfflinePairResult result,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(result);
        ValidatePairKey(result.PairKey);
        ValidateHash(result.DatasetHash);
        return WriteReplaceAsync(
            PairPath(result.PairKey),
            new AdaptiveOfflineCheckpoint
            {
                PairKey = result.PairKey,
                DatasetHash = result.DatasetHash,
                Fingerprint = Fingerprint(result),
                Result = result
            },
            cancellationToken);
    }

    public Task SaveReportAsync(
        AdaptiveOfflineReport report,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        if (report.SchemaVersion is not (
                AdaptiveOfflineSchema.ReportVersion or
                AdaptiveOfflineSchema.MultiBaselineReportVersion))
        {
            throw new InvalidOperationException(
                "Adaptive offline report uses an unsupported schema.");
        }
        ValidateHash(report.DatasetHash);
        return WriteReplaceAsync(ReportPath, report, cancellationToken);
    }

    public static void EnsureOutsideRepository(string outputDirectory, string repositoryPath)
    {
        var output = Path.GetFullPath(outputDirectory)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repository = Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (output.Equals(repository, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(repository + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Adaptive offline artifacts must be stored outside the evaluated repository.");
        }
    }

    private string PairPath(string pairKey) => Path.Combine(_pairsDirectory, pairKey + ".json");

    private static async Task<T> ReadAsync<T>(
        string path,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        return await JsonSerializer.DeserializeAsync<T>(
            stream,
            AdaptiveOfflineDatasetLoader.SerializerOptions,
            cancellationToken) ?? throw new InvalidOperationException(
                $"Adaptive offline artifact '{path}' is empty.");
    }

    private static async Task WriteNewAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            path,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            4096,
            FileOptions.Asynchronous | FileOptions.WriteThrough);
        await JsonSerializer.SerializeAsync(
            stream,
            value,
            AdaptiveOfflineDatasetLoader.SerializerOptions,
            cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteReplaceAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temporary = path + $".{Guid.NewGuid():N}.tmp";
        try
        {
            await WriteNewAsync(temporary, value, cancellationToken);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporary))
                File.Delete(temporary);
        }
    }

    private static string Fingerprint(AdaptiveOfflinePairResult result)
    {
        var json = JsonSerializer.Serialize(
            result,
            AdaptiveOfflineDatasetLoader.SerializerOptions);
        return "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(json)))
            .ToLowerInvariant();
    }

    private static void ValidatePairKey(string value)
    {
        if (value.Length != 64 || value.Any(character =>
                !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new InvalidOperationException("Adaptive offline pair key is invalid.");
        }
    }

    private static void ValidateHash(string value)
    {
        if (value.Length != 71 || !value.StartsWith("sha256:", StringComparison.Ordinal) ||
            value[7..].Any(character => !Uri.IsHexDigit(character) || char.IsUpper(character)))
        {
            throw new InvalidOperationException("Adaptive offline dataset hash is invalid.");
        }
    }

    private sealed class AdaptiveOfflineSession
    {
        public string SchemaVersion { get; init; } = AdaptiveOfflineSchema.SessionVersion;
        public string DatasetHash { get; init; } = string.Empty;
        public DateTime StartedAtUtc { get; init; }
    }

    private sealed class AdaptiveOfflineCheckpoint
    {
        public string SchemaVersion { get; init; } = AdaptiveOfflineSchema.CheckpointVersion;
        public string PairKey { get; init; } = string.Empty;
        public string DatasetHash { get; init; } = string.Empty;
        public string Fingerprint { get; init; } = string.Empty;
        public AdaptiveOfflinePairResult Result { get; init; } = new();
    }
}
