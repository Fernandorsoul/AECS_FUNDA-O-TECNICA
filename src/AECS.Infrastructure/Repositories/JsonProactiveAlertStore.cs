using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Repositories;

public sealed class JsonProactiveAlertStore : IProactiveAlertStore
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);
    private readonly string _rootPath;
    private readonly JsonSerializerOptions _jsonOptions;

    public JsonProactiveAlertStore(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        _rootPath = Path.GetFullPath(rootPath);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public static string GetDefaultRootPath()
    {
        var configured = Environment.GetEnvironmentVariable("AECS_ALERT_PATH");
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(configured);
        var localData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        if (string.IsNullOrWhiteSpace(localData))
            localData = Path.GetTempPath();
        return Path.Combine(localData, "AECS", "alerts");
    }

    public void EnsureRepositoryIsolation(string repositoryPath) =>
        EnsureOutsideRepository(_rootPath, repositoryPath, "Alert state path");

    public async Task<IReadOnlyList<ProactiveAlert>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        EnsureRepositoryIsolation(repositoryPath);
        var path = PathFor(repositoryPath);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            return await ReadAsync(path, repositoryPath, cancellationToken);
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<ProactiveAlert?> LoadAsync(
        string repositoryPath,
        Guid alertId,
        CancellationToken cancellationToken) =>
        (await ListAsync(repositoryPath, cancellationToken))
            .SingleOrDefault(item => item.Id == alertId);

    public async Task SaveAsync(
        ProactiveAlert alert,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(alert);
        Validate(alert);
        EnsureRepositoryIsolation(alert.RepositoryPath);
        Directory.CreateDirectory(_rootPath);
        var path = PathFor(alert.RepositoryPath);
        var gate = Gates.GetOrAdd(path, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var alerts = await ReadAsync(path, alert.RepositoryPath, cancellationToken);
            var index = alerts.FindIndex(item => item.Id == alert.Id);
            if (index >= 0)
                alerts[index] = alert;
            else
                alerts.Add(alert);
            alerts = alerts.OrderBy(item => item.CreatedAt).ThenBy(item => item.Id).ToList();
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(
                    temporaryPath,
                    JsonSerializer.Serialize(alerts, _jsonOptions),
                    cancellationToken);
                File.Move(temporaryPath, path, overwrite: true);
            }
            finally
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private async Task<List<ProactiveAlert>> ReadAsync(
        string path,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return [];
        var content = await File.ReadAllTextAsync(path, cancellationToken);
        var alerts = JsonSerializer.Deserialize<List<ProactiveAlert>>(content, _jsonOptions) ??
            throw new InvalidDataException("Local alert state is empty.");
        if (alerts.Select(item => item.Id).Distinct().Count() != alerts.Count)
            throw new InvalidDataException("Local alert state contains duplicate IDs.");
        foreach (var alert in alerts)
        {
            Validate(alert);
            if (!PathsEqual(alert.RepositoryPath, repositoryPath))
                throw new InvalidDataException("Local alert state crossed its repository boundary.");
        }
        return alerts;
    }

    private string PathFor(string repositoryPath)
    {
        var normalized = Normalize(repositoryPath);
        if (OperatingSystem.IsWindows())
            normalized = normalized.ToUpperInvariant();
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
        return Path.Combine(_rootPath, $"repository-{hash}.json");
    }

    private static void Validate(ProactiveAlert alert)
    {
        if (alert.SchemaVersion != ProactiveAlertSchema.AlertVersion ||
            alert.Id == Guid.Empty || string.IsNullOrWhiteSpace(alert.DeduplicationKey) ||
            string.IsNullOrWhiteSpace(alert.PolicyId) ||
            string.IsNullOrWhiteSpace(alert.RepositoryPath) ||
            string.IsNullOrWhiteSpace(alert.Recipient) ||
            alert.LatestEvidenceId == Guid.Empty || alert.EvidenceIds is null ||
            alert.EvidenceIds.Count == 0 ||
            !alert.EvidenceIds.Contains(alert.LatestEvidenceId) ||
            !Enum.IsDefined(alert.Kind) || !Enum.IsDefined(alert.Severity) ||
            !Enum.IsDefined(alert.Status) || alert.DeadlineAt == default ||
            alert.CreatedAt == default || alert.UpdatedAt == default || alert.Lifecycle is null)
        {
            throw new InvalidDataException("Local alert state is structurally invalid.");
        }
    }

    internal static void EnsureOutsideRepository(
        string targetPath,
        string repositoryPath,
        string description)
    {
        var target = Normalize(targetPath);
        var repository = Normalize(repositoryPath);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (string.Equals(target, repository, comparison) ||
            target.StartsWith(repository + Path.DirectorySeparatorChar, comparison))
        {
            throw new InvalidOperationException(
                $"{description} '{target}' must be outside target repository '{repository}'.");
        }
    }

    private static string Normalize(string path) => Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Normalize(left),
            Normalize(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
}
