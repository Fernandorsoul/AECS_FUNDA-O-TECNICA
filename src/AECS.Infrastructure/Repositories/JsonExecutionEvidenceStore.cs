using System.Text.Json;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Repositories;

public sealed class JsonExecutionEvidenceStore : IExecutionEvidenceStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

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

    private string GetEvidencePath(Guid evidenceId) =>
        Path.Combine(_rootPath, $"{evidenceId:N}.json");
}
