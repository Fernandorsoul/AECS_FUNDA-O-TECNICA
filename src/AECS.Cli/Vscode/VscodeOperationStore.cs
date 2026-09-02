using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AECS.Cli.Vscode;

public enum VscodeOperationStatus
{
    Queued,
    Running,
    CancellationRequested,
    Completed,
    Cancelled,
    Failed,
    Interrupted
}

public sealed class VscodeExecutionOperation
{
    public string SchemaVersion { get; set; } = "aecs.vscode-operation/v1";
    public Guid OperationId { get; set; }
    public Guid ClientRequestId { get; set; }
    public string RepositoryPath { get; set; } = string.Empty;
    public string TaskFile { get; set; } = string.Empty;
    public string TaskId { get; set; } = string.Empty;
    public string Objective { get; set; } = string.Empty;
    public VscodeOperationStatus Status { get; set; }
    public Guid? EvidenceId { get; set; }
    public string EvidenceLocation { get; set; } = string.Empty;
    public string Decision { get; set; } = string.Empty;
    public string FinalState { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public int OwnerProcessId { get; set; }
    public DateTime OwnerProcessStartedAt { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }

    [JsonIgnore]
    public bool IsTerminal => Status is VscodeOperationStatus.Completed or
        VscodeOperationStatus.Cancelled or VscodeOperationStatus.Failed or
        VscodeOperationStatus.Interrupted;
}

public sealed class VscodeOperationStore
{
    private readonly string _operationsDirectory;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly JsonSerializerOptions _jsonOptions;

    public VscodeOperationStore(string stateDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stateDirectory);
        _operationsDirectory = Path.Combine(Path.GetFullPath(stateDirectory), "operations");
        Directory.CreateDirectory(_operationsDirectory);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = true
        };
        _jsonOptions.Converters.Add(new JsonStringEnumConverter());
    }

    public async Task SaveAsync(
        VscodeExecutionOperation operation,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(operation);
        if (operation.OperationId == Guid.Empty)
            throw new ArgumentException("Operation id must be non-empty.", nameof(operation));
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(operation.OperationId);
            var temporaryPath = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            await File.WriteAllTextAsync(
                temporaryPath,
                JsonSerializer.Serialize(operation, _jsonOptions),
                cancellationToken);
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VscodeExecutionOperation?> LoadAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            return null;
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var path = PathFor(operationId);
            if (!File.Exists(path))
                return null;
            return JsonSerializer.Deserialize<VscodeExecutionOperation>(
                await File.ReadAllTextAsync(path, cancellationToken),
                _jsonOptions);
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<VscodeExecutionOperation?> FindByClientRequestAsync(
        Guid clientRequestId,
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        var operations = await ListAsync(cancellationToken);
        return operations
            .Where(item => item.ClientRequestId == clientRequestId &&
                PathsEqual(item.RepositoryPath, repositoryPath))
            .OrderByDescending(item => item.CreatedAt)
            .FirstOrDefault();
    }

    public async Task RecoverInterruptedAsync(
        string repositoryPath,
        CancellationToken cancellationToken)
    {
        foreach (var operation in await ListAsync(cancellationToken))
        {
            if (!PathsEqual(operation.RepositoryPath, repositoryPath) || operation.IsTerminal ||
                IsOwnerAlive(operation))
            {
                continue;
            }
            operation.Status = VscodeOperationStatus.Interrupted;
            operation.Message = "The backend stopped before the execution reached a durable terminal state.";
            operation.UpdatedAt = DateTime.UtcNow;
            await SaveAsync(operation, cancellationToken);
        }
    }

    private async Task<List<VscodeExecutionOperation>> ListAsync(
        CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            var result = new List<VscodeExecutionOperation>();
            foreach (var path in Directory.EnumerateFiles(_operationsDirectory, "*.json"))
            {
                try
                {
                    var item = JsonSerializer.Deserialize<VscodeExecutionOperation>(
                        await File.ReadAllTextAsync(path, cancellationToken),
                        _jsonOptions);
                    if (item is not null)
                        result.Add(item);
                }
                catch (JsonException)
                {
                    // A corrupt operation is ignored; authenticated execution evidence remains authoritative.
                }
            }
            return result;
        }
        finally
        {
            _gate.Release();
        }
    }

    private string PathFor(Guid operationId) =>
        Path.Combine(_operationsDirectory, operationId.ToString("N") + ".json");

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private static bool IsOwnerAlive(VscodeExecutionOperation operation)
    {
        try
        {
            using var process = Process.GetProcessById(operation.OwnerProcessId);
            return process.StartTime.ToUniversalTime() == operation.OwnerProcessStartedAt;
        }
        catch
        {
            return false;
        }
    }
}
