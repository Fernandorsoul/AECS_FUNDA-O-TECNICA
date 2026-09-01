using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using AECS.Application.Jarvis;
using AECS.Application.Parsing;
using AECS.Cli.Runtime;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Cli.Vscode;

public sealed class VscodeStartExecutionParameters
{
    public Guid ClientRequestId { get; init; }
    public string TaskFile { get; init; } = string.Empty;
}

public sealed class VscodeOperationParameters
{
    public Guid OperationId { get; init; }
}

public sealed class VscodeReviewParameters
{
    public Guid OperationId { get; init; }
    public string ExpectedDiffHash { get; init; } = string.Empty;
    public string Decision { get; init; } = string.Empty;
    public string Justification { get; init; } = string.Empty;
    public string PolicyReference { get; init; } = string.Empty;
    public int ValidMinutes { get; init; } = 15;
}

public sealed class VscodeEvidenceLink
{
    public string Relation { get; init; } = string.Empty;
    public string Uri { get; init; } = string.Empty;
}

public sealed class VscodeExecutionInspection
{
    public string SchemaVersion { get; init; } = "aecs.vscode-inspection/v1";
    public VscodeExecutionOperation Operation { get; init; } = new();
    public CandidateReviewSnapshot Candidate { get; init; } = new();
    public IReadOnlyList<DurableGateFact> Gates { get; init; } = [];
    public IReadOnlyList<DurableAcceptanceFact> AcceptanceCriteria { get; init; } = [];
    public DurableBudgetFact Budget { get; init; } = new();
    public IReadOnlyList<VscodeEvidenceLink> EvidenceLinks { get; init; } = [];
    public IReadOnlyList<string> Diagnostics { get; init; } = [];
}

public sealed class VscodeExecutionBackend : IVscodeProtocolHandler, IAsyncDisposable
{
    private readonly string _repositoryPath;
    private readonly AecsExecutionRuntime _runtime;
    private readonly VscodeOperationStore _operations;
    private readonly ConcurrentDictionary<Guid, ActiveExecution> _active = new();
    private readonly JsonSerializerOptions _jsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };
    private readonly int _processId;
    private readonly DateTime _processStartedAt;

    public VscodeExecutionBackend(
        string repositoryPath,
        AecsExecutionRuntime runtime,
        VscodeOperationStore operations)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repositoryPath);
        _repositoryPath = Path.GetFullPath(repositoryPath);
        _runtime = runtime ?? throw new ArgumentNullException(nameof(runtime));
        _operations = operations ?? throw new ArgumentNullException(nameof(operations));
        using var process = Process.GetCurrentProcess();
        _processId = process.Id;
        _processStartedAt = process.StartTime.ToUniversalTime();
    }

    public Task RecoverAsync(CancellationToken cancellationToken) =>
        _operations.RecoverInterruptedAsync(_repositoryPath, cancellationToken);

    public async Task<object?> HandleAsync(
        string method,
        JsonElement parameters,
        CancellationToken cancellationToken) => method switch
    {
        "initialize" => Initialize(),
        "execution.start" => await StartAsync(
            ReadParameters<VscodeStartExecutionParameters>(parameters),
            cancellationToken),
        "execution.get" => await GetAsync(
            ReadParameters<VscodeOperationParameters>(parameters),
            cancellationToken),
        "execution.cancel" => await CancelAsync(
            ReadParameters<VscodeOperationParameters>(parameters),
            cancellationToken),
        "execution.inspect" => await InspectAsync(
            ReadParameters<VscodeOperationParameters>(parameters),
            cancellationToken),
        "review.submit" => await ReviewAsync(
            ReadParameters<VscodeReviewParameters>(parameters),
            cancellationToken),
        _ => throw new VscodeProtocolException("method_not_found", $"Unknown protocol method '{method}'.")
    };

    public async ValueTask DisposeAsync()
    {
        var active = _active.Values.ToArray();
        foreach (var item in active)
            item.Cancellation.Cancel();
        try
        {
            await Task.WhenAll(active.Select(item => item.Task));
        }
        catch
        {
            // Each operation persists its own terminal result before completing.
        }
        foreach (var item in active)
            item.Cancellation.Dispose();
    }

    private object Initialize() => new
    {
        schemaVersion = "aecs.vscode-capabilities/v1",
        protocol = VscodeProtocolConstants.Version,
        repositoryPath = _repositoryPath,
        actor = Environment.UserName,
        capabilities = new[]
        {
            "execution.start",
            "execution.get",
            "execution.cancel",
            "execution.inspect",
            "review.submit"
        },
        trustBoundary = new
        {
            clientMayApplyPatch = false,
            reviewIsPersistedByBackend = true,
            promotionRequiresSeparateBackendConfirmation = true
        }
    };

    private async Task<VscodeExecutionOperation> StartAsync(
        VscodeStartExecutionParameters parameters,
        CancellationToken cancellationToken)
    {
        if (parameters.ClientRequestId == Guid.Empty)
            throw new VscodeProtocolException("invalid_parameters", "clientRequestId must be a non-empty GUID.");
        if (string.IsNullOrWhiteSpace(parameters.TaskFile))
            throw new VscodeProtocolException("invalid_parameters", "taskFile is required.");

        var existing = await _operations.FindByClientRequestAsync(
            parameters.ClientRequestId,
            _repositoryPath,
            cancellationToken);
        if (existing is not null)
            return existing;
        if (!_active.IsEmpty)
            throw new VscodeProtocolException(
                "execution_in_progress",
                "Only one execution may mutate a repository workspace at a time.");

        var taskFile = Path.GetFullPath(parameters.TaskFile);
        var extension = Path.GetExtension(taskFile);
        if (!File.Exists(taskFile) ||
            !string.Equals(extension, ".yaml", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(extension, ".yml", StringComparison.OrdinalIgnoreCase))
        {
            throw new VscodeProtocolException(
                "invalid_task_file",
                "taskFile must reference an existing YAML TaskContract.");
        }

        TaskContract contract;
        try
        {
            contract = new TaskContractParser().ParseFromFile(taskFile);
        }
        catch (Exception ex)
        {
            throw new VscodeProtocolException(
                "invalid_task_contract",
                $"TaskContract could not be parsed: {ex.Message}");
        }

        var now = DateTime.UtcNow;
        var operation = new VscodeExecutionOperation
        {
            OperationId = Guid.NewGuid(),
            ClientRequestId = parameters.ClientRequestId,
            RepositoryPath = _repositoryPath,
            TaskFile = taskFile,
            TaskId = contract.Id,
            Objective = contract.Objective,
            Status = VscodeOperationStatus.Queued,
            Message = "Execution accepted by the AECS backend.",
            OwnerProcessId = _processId,
            OwnerProcessStartedAt = _processStartedAt,
            CreatedAt = now,
            UpdatedAt = now
        };
        await _operations.SaveAsync(operation, cancellationToken);

        var active = new ActiveExecution();
        if (!_active.TryAdd(operation.OperationId, active))
            throw new VscodeProtocolException("operation_conflict", "The operation id is already active.");
        active.Task = ExecuteAsync(operation, contract, active.Cancellation.Token);
        return operation;
    }

    private async Task ExecuteAsync(
        VscodeExecutionOperation operation,
        TaskContract contract,
        CancellationToken cancellationToken)
    {
        try
        {
            operation.Status = VscodeOperationStatus.Running;
            operation.Message = "Execution is running inside the AECS trust boundary.";
            operation.UpdatedAt = DateTime.UtcNow;
            await _operations.SaveAsync(operation, CancellationToken.None);

            var result = await _runtime.CreatePipeline().RunAsync(
                _repositoryPath,
                contract,
                cancellationToken);
            operation.Status = VscodeOperationStatus.Completed;
            operation.EvidenceId = result.EvidenceId;
            operation.EvidenceLocation = result.EvidenceLocation;
            operation.Decision = result.Decision.Decision.ToString();
            operation.FinalState = result.FinalState.ToString();
            operation.Message = "Execution completed; authenticated evidence is available.";
        }
        catch (OperationCanceledException)
        {
            operation.Status = VscodeOperationStatus.Cancelled;
            operation.Message = "Execution was cancelled through the AECS backend.";
        }
        catch (Exception ex)
        {
            operation.Status = VscodeOperationStatus.Failed;
            operation.Message = $"Execution failed closed: {ex.Message}";
        }
        finally
        {
            operation.UpdatedAt = DateTime.UtcNow;
            await _operations.SaveAsync(operation, CancellationToken.None);
            if (_active.TryRemove(operation.OperationId, out var active))
                active.Cancellation.Dispose();
        }
    }

    private async Task<VscodeExecutionOperation> GetAsync(
        VscodeOperationParameters parameters,
        CancellationToken cancellationToken) =>
        await LoadScopedAsync(parameters.OperationId, cancellationToken);

    private async Task<VscodeExecutionOperation> CancelAsync(
        VscodeOperationParameters parameters,
        CancellationToken cancellationToken)
    {
        var operation = await LoadScopedAsync(parameters.OperationId, cancellationToken);
        if (operation.IsTerminal)
            return operation;
        if (!_active.TryGetValue(operation.OperationId, out var active))
            throw new VscodeProtocolException(
                "execution_not_attached",
                "The operation is not attached to this backend process; its persisted linkage was preserved.");
        active.Cancellation.Cancel();
        operation.Status = VscodeOperationStatus.CancellationRequested;
        operation.Message = "Cancellation was requested from the AECS backend.";
        operation.UpdatedAt = DateTime.UtcNow;
        await _operations.SaveAsync(operation, cancellationToken);
        return operation;
    }

    private async Task<VscodeExecutionInspection> InspectAsync(
        VscodeOperationParameters parameters,
        CancellationToken cancellationToken)
    {
        var operation = await LoadScopedAsync(parameters.OperationId, cancellationToken);
        if (operation.EvidenceId is not { } evidenceId)
            throw new VscodeProtocolException("evidence_unavailable", "The operation has no durable evidence yet.");

        var candidate = await _runtime.CreatePromotionService().InspectAsync(
            evidenceId,
            _repositoryPath,
            cancellationToken);
        if (!candidate.Available)
            throw new VscodeProtocolException("evidence_unavailable", candidate.Message);

        var diagnostics = new List<string>();
        var gates = new List<DurableGateFact>();
        var acceptance = new List<DurableAcceptanceFact>();
        var budget = new DurableBudgetFact();
        if (_runtime.EvidenceStore is IEvidenceGraphSource graphSource)
        {
            var explanation = await new DurableExecutionHistoryService(
                _runtime.EvidenceStore,
                graphSource,
                _repositoryPath,
                Environment.UserName).ExplainAsync(
                    new DurableHistoryQuery { EvidenceId = evidenceId },
                    cancellationToken);
            if (explanation.Item is not null)
            {
                gates = explanation.Item.Persisted.Gates;
                acceptance = explanation.Item.Persisted.AcceptanceCriteria;
                budget = explanation.Item.Persisted.Budget;
            }
            diagnostics.AddRange(explanation.Diagnostics);
        }
        else
        {
            diagnostics.Add("The selected evidence store does not expose authenticated history queries.");
        }

        return new VscodeExecutionInspection
        {
            Operation = operation,
            Candidate = candidate,
            Gates = gates,
            AcceptanceCriteria = acceptance,
            Budget = budget,
            EvidenceLinks =
            [
                new VscodeEvidenceLink
                {
                    Relation = "evidence",
                    Uri = $"aecs://evidence/{candidate.EvidenceId:N}"
                },
                new VscodeEvidenceLink
                {
                    Relation = "candidate",
                    Uri = $"aecs://candidate/{candidate.CandidateId:N}"
                },
                new VscodeEvidenceLink
                {
                    Relation = "operation",
                    Uri = $"aecs://operation/{operation.OperationId:N}"
                }
            ],
            Diagnostics = diagnostics
        };
    }

    private async Task<CandidateReviewResult> ReviewAsync(
        VscodeReviewParameters parameters,
        CancellationToken cancellationToken)
    {
        var operation = await LoadScopedAsync(parameters.OperationId, cancellationToken);
        if (operation.EvidenceId is not { } evidenceId)
            throw new VscodeProtocolException("evidence_unavailable", "The operation has no durable evidence yet.");
        if (parameters.ValidMinutes is < 1 or > 1440)
            throw new VscodeProtocolException("invalid_parameters", "validMinutes must be between 1 and 1440.");
        if (string.IsNullOrWhiteSpace(parameters.ExpectedDiffHash) ||
            string.IsNullOrWhiteSpace(parameters.Justification) ||
            string.IsNullOrWhiteSpace(parameters.PolicyReference))
        {
            throw new VscodeProtocolException(
                "invalid_parameters",
                "expectedDiffHash, justification and policyReference are required.");
        }
        if (!Enum.TryParse<CandidateReviewDecision>(
                parameters.Decision,
                ignoreCase: true,
                out var decision) ||
            decision is not (CandidateReviewDecision.Approve or CandidateReviewDecision.Reject))
        {
            throw new VscodeProtocolException(
                "invalid_parameters",
                "decision must be Approve or Reject.");
        }

        return await _runtime.CreatePromotionService().ReviewAsync(new CandidateReviewRequest
        {
            EvidenceId = evidenceId,
            RepositoryPath = _repositoryPath,
            ExpectedDiffHash = parameters.ExpectedDiffHash.Trim(),
            Actor = Environment.UserName,
            Decision = decision,
            Justification = parameters.Justification.Trim(),
            ValidUntil = DateTime.UtcNow.AddMinutes(parameters.ValidMinutes),
            PolicyReference = parameters.PolicyReference.Trim()
        }, cancellationToken);
    }

    private async Task<VscodeExecutionOperation> LoadScopedAsync(
        Guid operationId,
        CancellationToken cancellationToken)
    {
        if (operationId == Guid.Empty)
            throw new VscodeProtocolException("invalid_parameters", "operationId must be a non-empty GUID.");
        var operation = await _operations.LoadAsync(operationId, cancellationToken);
        if (operation is null || !PathsEqual(operation.RepositoryPath, _repositoryPath))
            throw new VscodeProtocolException("operation_not_found", "The operation was not found for this repository.");
        return operation;
    }

    private T ReadParameters<T>(JsonElement parameters)
    {
        if (parameters.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null)
            throw new VscodeProtocolException("invalid_parameters", "A parameters object is required.");
        try
        {
            return parameters.Deserialize<T>(_jsonOptions) ??
                throw new VscodeProtocolException("invalid_parameters", "Parameters are empty.");
        }
        catch (JsonException)
        {
            throw new VscodeProtocolException("invalid_parameters", "Parameters do not match the protocol contract.");
        }
    }

    private static bool PathsEqual(string left, string right) =>
        string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);

    private sealed class ActiveExecution
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task Task { get; set; } = Task.CompletedTask;
    }
}
