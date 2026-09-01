using AECS.Application;
using AECS.Application.Classification;
using AECS.Application.Experiments;
using AECS.Application.Jarvis;
using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;
using AECS.Infrastructure.Sandbox;

namespace AECS.Cli.Jarvis;

public class JarvisRepl
{
    private readonly string _repoPath;
    private readonly bool _useMock;
    private readonly IExecutionEvidenceStore _evidenceStore;
    private readonly bool _allowHostExecution;
    private readonly TaskContractParser _parser = new();
    private readonly RiskClassifier _riskClassifier = new();
    private readonly DurableExecutionHistoryService? _durableHistory;

    public JarvisRepl(
        string repoPath,
        bool useMock,
        IExecutionEvidenceStore? evidenceStore = null,
        bool allowHostExecution = false)
    {
        _repoPath = repoPath;
        _useMock = useMock;
        _evidenceStore = evidenceStore ?? new JsonExecutionEvidenceStore(
            JsonExecutionEvidenceStore.GetDefaultRootPath());
        _allowHostExecution = allowHostExecution;
        if (_evidenceStore is IEvidenceGraphSource graphSource)
        {
            _durableHistory = new DurableExecutionHistoryService(
                _evidenceStore,
                graphSource,
                repoPath,
                Environment.UserName);
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        Console.WriteLine("AECS — Agentic Engineering Control System");
        Console.WriteLine("Type 'help' for commands, 'exit' to quit.");
        Console.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            Console.Write("aecs> ");
            var input = Console.ReadLine();

            if (input is null)
                break;

            input = input.Trim();
            if (string.IsNullOrEmpty(input))
                continue;

            var parts = input.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
            var command = parts[0].ToLowerInvariant();
            var argument = parts.Length > 1 ? parts[1] : "";

            try
            {
                var shouldExit = await ExecuteCommandAsync(command, argument, cancellationToken);
                if (shouldExit)
                    break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine();
        }
    }

    private async Task<bool> ExecuteCommandAsync(string command, string argument, CancellationToken ct)
    {
        switch (command)
        {
            case "help":
                ShowHelp();
                return false;

            case "run":
                await RunTask(argument, ct);
                return false;

            case "experiment":
                await RunExperiment(argument, ct);
                return false;

            case "status":
                await ShowStatus(argument, ct);
                return false;

            case "history":
                await ShowHistory(argument, ct);
                return false;

            case "explain":
                await ExplainTask(argument, ct);
                return false;

            case "risk":
                ClassifyRisk(argument);
                return false;

            case "context":
                await ShowContext(argument, ct);
                return false;

            case "exit" or "quit":
                Console.WriteLine("Goodbye.");
                return true;

            default:
                Console.WriteLine($"Unknown command: '{command}'. Type 'help' for available commands.");
                return false;
        }
    }

    private void ShowHelp()
    {
        Console.WriteLine("Commands:");
        Console.WriteLine("  run <task-file>     Execute a single task");
        Console.WriteLine("  experiment <dir>    Run experiment on task directory");
        Console.WriteLine("  status [--json]     Show latest persisted execution");
        Console.WriteLine("  history [filters]   Query authenticated execution history");
        Console.WriteLine("  explain <task-id>   Explain persisted evidence (--json supported)");
        Console.WriteLine("  risk <objective>    Classify risk for an objective");
        Console.WriteLine("  context <task-id>   Show persisted context manifest (--json supported)");
        Console.WriteLine("  filters: --task <id> --run <id> --candidate <id> --evidence <id> --limit <n>");
        Console.WriteLine("  exit                Quit AECS");
    }

    private async Task RunTask(string taskFile, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(taskFile))
        {
            Console.WriteLine("Usage: run <task-file>");
            return;
        }

        var contract = _parser.ParseFromFile(taskFile);
        IAgentAdapter agent = _useMock
            ? new MockAgentAdapter()
            : new OllamaAdapter(new HttpClient());

        var execution = await CreatePipeline(agent).RunAsync(_repoPath, contract, ct);

        Console.WriteLine($"Decision: {execution.Decision.Decision}");
        Console.WriteLine($"Candidate: {execution.CandidateChangeSet.Id:N}");
        Console.WriteLine($"Original repository unchanged: {execution.OriginalRepositoryUnchanged}");
        Console.WriteLine($"Evidence: {execution.EvidenceLocation}");
    }

    private async Task RunExperiment(string tasksDir, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(tasksDir))
        {
            Console.WriteLine("Usage: experiment <tasks-dir>");
            return;
        }

        var taskFiles = Directory.GetFiles(tasksDir, "*.yaml")
            .Concat(Directory.GetFiles(tasksDir, "*.yml"))
            .OrderBy(f => f)
            .ToList();

        if (taskFiles.Count == 0)
        {
            Console.WriteLine($"No YAML files found in {tasksDir}");
            return;
        }

        IAgentAdapter agent = _useMock
            ? new MockAgentAdapter()
            : new OllamaAdapter(new HttpClient());

        var runner = new ExperimentRunner(CreatePipeline(agent));
        var report = await runner.RunAsync(_repoPath, taskFiles, ct);

        Console.WriteLine(ExperimentReportFormatter.Format(report));

    }

    private StagedExecutionPipeline CreatePipeline(IAgentAdapter agent)
    {
        var processRunner = new SystemProcessRunner();
        return new StagedExecutionPipeline(
            agent,
            processRunner,
            _evidenceStore,
            stagedProcessRunnerFactory: new DockerStagedProcessRunnerFactory(
                processRunner,
                _allowHostExecution));
    }

    private async Task ShowStatus(string argument, CancellationToken cancellationToken)
    {
        if (!EnsureDurableHistory())
            return;
        if (!TryParseLookup(argument, allowLegacyTask: false, out var options, out var error) ||
            !IsUnfiltered(options.Query) || options.Query.Limit != 50)
        {
            Console.WriteLine($"Usage: status [--json]. {error}");
            return;
        }
        var result = await _durableHistory!.StatusAsync(cancellationToken);
        Console.Write(options.Json
            ? DurableExecutionHistoryFormatter.ToJson(result) + Environment.NewLine
            : DurableExecutionHistoryFormatter.HistoryToText(result, status: true));
    }

    private async Task ShowHistory(string argument, CancellationToken cancellationToken)
    {
        if (!EnsureDurableHistory())
            return;
        if (!TryParseLookup(argument, allowLegacyTask: false, out var options, out var error))
        {
            Console.WriteLine(
                "Usage: history [--task <id>] [--run <id>] [--candidate <id>] " +
                "[--evidence <id>] [--limit <1-500>] [--json]. " + error);
            return;
        }
        var result = await _durableHistory!.QueryAsync(options.Query, cancellationToken);
        Console.Write(options.Json
            ? DurableExecutionHistoryFormatter.ToJson(result) + Environment.NewLine
            : DurableExecutionHistoryFormatter.HistoryToText(result));
    }

    private async Task ExplainTask(string argument, CancellationToken cancellationToken)
    {
        if (!EnsureDurableHistory())
            return;
        if (!TryParseLookup(argument, allowLegacyTask: true, out var options, out var error) ||
            IsUnfiltered(options.Query))
        {
            Console.WriteLine(
                "Usage: explain <task-id> | --task <id> | --run <id> | " +
                "--candidate <id> | --evidence <id> [--json]. " + error);
            return;
        }
        var result = await _durableHistory!.ExplainAsync(options.Query, cancellationToken);
        Console.Write(options.Json
            ? DurableExecutionHistoryFormatter.ToJson(result) + Environment.NewLine
            : DurableExecutionHistoryFormatter.ExplanationToText(result));
    }

    private void ClassifyRisk(string objective)
    {
        if (string.IsNullOrEmpty(objective))
        {
            Console.WriteLine("Usage: risk <objective>");
            return;
        }

        var contract = new TaskContract
        {
            Id = "TEMP",
            Objective = objective,
            Scope = new ScopeDefinition { Allowed = ["src/**"] }
        };

        var risk = _riskClassifier.Classify(contract);
        Console.WriteLine($"Risk: {risk}");
        Console.WriteLine($"Objective: {objective}");
    }

    private async Task ShowContext(string argument, CancellationToken cancellationToken)
    {
        if (!EnsureDurableHistory())
            return;
        if (!TryParseLookup(argument, allowLegacyTask: true, out var options, out var error) ||
            IsUnfiltered(options.Query))
        {
            Console.WriteLine(
                "Usage: context <task-id> | --task <id> | --run <id> | " +
                "--candidate <id> | --evidence <id> [--json]. " + error);
            return;
        }
        var result = await _durableHistory!.ContextAsync(options.Query, cancellationToken);
        Console.Write(options.Json
            ? DurableExecutionHistoryFormatter.ToJson(result) + Environment.NewLine
            : DurableExecutionHistoryFormatter.ContextToText(result));
    }

    private bool EnsureDurableHistory()
    {
        if (_durableHistory is not null)
            return true;
        Console.WriteLine(
            "Authenticated durable history is unavailable for the selected evidence store.");
        return false;
    }

    private static bool IsUnfiltered(DurableHistoryQuery query) =>
        query.EvidenceId is null && query.TaskId is null &&
        query.RunId is null && query.CandidateId is null;

    private static bool TryParseLookup(
        string argument,
        bool allowLegacyTask,
        out JarvisLookupOptions options,
        out string error)
    {
        var tokens = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? taskId = null;
        Guid? evidenceId = null;
        Guid? runId = null;
        Guid? candidateId = null;
        var limit = 50;
        var json = false;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token == "--json")
            {
                json = true;
                continue;
            }
            if (token == "--format" && index + 1 < tokens.Length)
            {
                var format = tokens[++index];
                if (format is not ("json" or "text"))
                {
                    options = new JarvisLookupOptions();
                    error = "Format must be text or json.";
                    return false;
                }
                json = format == "json";
                continue;
            }
            if (token == "--task" && index + 1 < tokens.Length)
            {
                taskId = tokens[++index];
                continue;
            }
            if (token == "--limit" && index + 1 < tokens.Length &&
                int.TryParse(tokens[++index], out var parsedLimit) && parsedLimit is >= 1 and <= 500)
            {
                limit = parsedLimit;
                continue;
            }
            if ((token is "--evidence" or "--run" or "--candidate") &&
                index + 1 < tokens.Length && Guid.TryParse(tokens[++index], out var parsedId))
            {
                if (token == "--evidence")
                    evidenceId = parsedId;
                else if (token == "--run")
                    runId = parsedId;
                else
                    candidateId = parsedId;
                continue;
            }
            if (allowLegacyTask && !token.StartsWith("--", StringComparison.Ordinal) &&
                taskId is null)
            {
                taskId = token;
                continue;
            }
            options = new JarvisLookupOptions();
            error = $"Invalid argument '{token}'.";
            return false;
        }
        if (evidenceId.HasValue &&
            (taskId is not null || runId.HasValue || candidateId.HasValue))
        {
            options = new JarvisLookupOptions();
            error = "--evidence cannot be combined with other execution identifiers.";
            return false;
        }
        options = new JarvisLookupOptions
        {
            Json = json,
            Query = new DurableHistoryQuery
            {
                EvidenceId = evidenceId,
                TaskId = taskId,
                RunId = runId,
                CandidateId = candidateId,
                Limit = limit
            }
        };
        error = string.Empty;
        return true;
    }

    private sealed class JarvisLookupOptions
    {
        public bool Json { get; init; }
        public DurableHistoryQuery Query { get; init; } = new();
    }
}
