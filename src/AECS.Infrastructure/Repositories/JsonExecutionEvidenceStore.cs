using System.Collections.Concurrent;
using System.Text.Json;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Repositories;

public sealed class JsonExecutionEvidenceStore : IExecutionEvidenceStore
{
    private static readonly TimeSpan EvidenceLockTimeout = TimeSpan.FromSeconds(10);
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> WriteLocks = new(
        OperatingSystem.IsWindows()
            ? StringComparer.OrdinalIgnoreCase
            : StringComparer.Ordinal);

    private readonly string _rootPath;

    public JsonExecutionEvidenceStore(string rootPath)
    {
        _rootPath = Path.GetFullPath(rootPath);
    }

    public static string GetDefaultRootPath()
    {
        var configuredPath = Environment.GetEnvironmentVariable("AECS_EVIDENCE_PATH");
        if (!string.IsNullOrWhiteSpace(configuredPath))
            return configuredPath;

        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.GetTempPath();

        return Path.Combine(localData, "AECS", "evidence");
    }

    public void EnsureRepositoryIsolation(string repositoryPath)
    {
        var repositoryRoot = Path.GetFullPath(repositoryPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var repositoryPrefix = repositoryRoot + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (string.Equals(_rootPath, repositoryRoot, comparison) ||
            _rootPath.StartsWith(repositoryPrefix, comparison))
        {
            throw new InvalidOperationException(
                $"Evidence path '{_rootPath}' must be outside target repository '{repositoryRoot}'.");
        }
    }

    public async Task<string> SaveAsync(
        ExecutionEvidence evidence,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_rootPath);

        var targetPath = GetEvidencePath(evidence.Id);
        var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
        var json = JsonSerializer.Serialize(evidence, SerializerOptions);

        try
        {
            await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
            File.Move(temporaryPath, targetPath, overwrite: false);
            return targetPath;
        }
        finally
        {
            if (File.Exists(temporaryPath))
                File.Delete(temporaryPath);
        }
    }

    public async Task<ExecutionEvidence?> LoadAsync(
        Guid evidenceId,
        CancellationToken cancellationToken)
    {
        var path = GetEvidencePath(evidenceId);
        if (!File.Exists(path))
            return null;

        await using var stream = File.OpenRead(path);
        return await JsonSerializer.DeserializeAsync<ExecutionEvidence>(
            stream,
            SerializerOptions,
            cancellationToken);
    }

    public async Task AppendPromotionAsync(
        Guid evidenceId,
        CandidatePromotionEvidence promotion,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(promotion);
        var targetPath = GetEvidencePath(evidenceId);
        var writeLock = WriteLocks.GetOrAdd(targetPath, _ => new SemaphoreSlim(1, 1));
        await writeLock.WaitAsync(cancellationToken);
        try
        {
            await using var fileLock = await AcquireFileLockAsync(
                targetPath + ".lock",
                cancellationToken);
            if (!File.Exists(targetPath))
                throw new FileNotFoundException("Execution evidence not found.", targetPath);

            ExecutionEvidence evidence;
            await using (var stream = File.OpenRead(targetPath))
            {
                evidence = await JsonSerializer.DeserializeAsync<ExecutionEvidence>(
                        stream,
                        SerializerOptions,
                        cancellationToken)
                    ?? throw new InvalidOperationException("Execution evidence JSON is invalid.");
            }

            if (evidence.Id != evidenceId)
                throw new InvalidOperationException("Execution evidence ID does not match its file name.");
            if (evidence.Promotions.Any(item => item.Id == promotion.Id))
                throw new InvalidOperationException("Promotion evidence has already been recorded.");

            evidence.Promotions.Add(promotion);
            var temporaryPath = targetPath + $".{Guid.NewGuid():N}.tmp";
            try
            {
                var json = JsonSerializer.Serialize(evidence, SerializerOptions);
                await File.WriteAllTextAsync(temporaryPath, json, cancellationToken);
                File.Move(temporaryPath, targetPath, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            writeLock.Release();
        }
    }

    private static async Task<FileStream> AcquireFileLockAsync(
        string lockPath,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + EvidenceLockTimeout;
        while (true)
        {
            try
            {
                return new FileStream(
                    lockPath,
                    FileMode.OpenOrCreate,
                    FileAccess.ReadWrite,
                    FileShare.None,
                    bufferSize: 1,
                    FileOptions.Asynchronous);
            }
            catch (IOException) when (DateTime.UtcNow < deadline)
            {
                await Task.Delay(TimeSpan.FromMilliseconds(50), cancellationToken);
            }
        }
    }

    private string GetEvidencePath(Guid evidenceId) =>
        Path.Combine(_rootPath, $"{evidenceId:N}.json");
}
