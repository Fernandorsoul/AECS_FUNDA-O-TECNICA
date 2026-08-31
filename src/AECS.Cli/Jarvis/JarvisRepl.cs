using AECS.Application;
using AECS.Application.Classification;
using AECS.Application.ContextCompiler;
using AECS.Application.ControlKernel;
using AECS.Application.Experiments;
using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;

namespace AECS.Cli.Jarvis;

public class JarvisRepl
{
    private readonly string _repoPath;
    private readonly bool _useMock;
    private readonly IExecutionEvidenceStore _evidenceStore;
    private readonly TaskContractParser _parser = new();
    private readonly RiskClassifier _riskClassifier = new();
    private readonly ExecutionController _executionController = new();
    private readonly ControlKernel _kernel = new();
    private readonly DecisionEngine _decisionEngine = new();
    private readonly List<TaskExperimentResult> _history = [];
    private TaskExperimentResult? _lastResult;

    public JarvisRepl(
        string repoPath,
        bool useMock,
        IExecutionEvidenceStore? evidenceStore = null)
    {
        _repoPath = repoPath;
        _useMock = useMock;
        _evidenceStore = evidenceStore ?? new JsonExecutionEvidenceStore(
            JsonExecutionEvidenceStore.GetDefaultRootPath());
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
                ShowStatus();
                return false;

            case "history":
                ShowHistory();
                return false;

            case "explain":
                ExplainTask(argument);
                return false;

            case "risk":
                ClassifyRisk(argument);
                return false;

            case "context":
                ShowContext(argument);
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
        Console.WriteLine("  status              Show last run status");
        Console.WriteLine("  history             Show execution history");
        Console.WriteLine("  explain <task-id>   Explain what happened in a task");
        Console.WriteLine("  risk <objective>    Classify risk for an objective");
        Console.WriteLine("  context <task-id>   Show context package for a task");
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

        var result = new TaskExperimentResult
        {
            TaskId = execution.Contract.Id,
            Objective = execution.Contract.Objective,
            Risk = execution.Risk,
            Model = execution.Model,
            Decision = execution.Decision.Decision,
            DecisionReason = execution.Decision.Reason,
            Duration = execution.BudgetUsage.WallClockElapsed,
            InputTokens = execution.AgentResult.InputTokens,
            OutputTokens = execution.AgentResult.OutputTokens,
            EstimatedCost = execution.AgentResult.EstimatedCost,
            FilesChanged = execution.CandidateChangeSet.ChangedFiles.Count,
            Verifications = execution.VerificationResults.ToDictionary(
                verification => verification.Verifier,
                verification => verification.Status),
            AcceptanceCriteria = execution.AcceptanceCriteriaResults.ToList(),
            AgentAttempts = execution.AgentAttempts.ToList(),
            BudgetUsage = execution.BudgetUsage,
            RetryCount = execution.AgentRun.RetryCount,
            EvidenceId = execution.EvidenceId,
            OriginalRepositoryUnchanged = execution.OriginalRepositoryUnchanged
        };

        _history.Add(result);
        _lastResult = result;

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

        _history.AddRange(report.Results);
        if (report.Results.Count > 0)
            _lastResult = report.Results.Last();
    }

    private StagedExecutionPipeline CreatePipeline(IAgentAdapter agent)
    {
        var processRunner = new SystemProcessRunner();
        return new StagedExecutionPipeline(agent, processRunner, _evidenceStore);
    }

    private void ShowStatus()
    {
        if (_lastResult is null)
        {
            Console.WriteLine("No executions yet. Use 'run' or 'experiment' first.");
            return;
        }

        Console.WriteLine($"Last execution: {_lastResult.TaskId}");
        Console.WriteLine($"Objective: {_lastResult.Objective}");
        Console.WriteLine($"Risk: {_lastResult.Risk}");
        Console.WriteLine($"Model: {_lastResult.Model}");
        Console.WriteLine($"Decision: {_lastResult.Decision}");
        Console.WriteLine($"Duration: {_lastResult.Duration.TotalSeconds:F1}s");
        Console.WriteLine($"Cost: ${_lastResult.EstimatedCost:F2}");
    }

    private void ShowHistory()
    {
        if (_history.Count == 0)
        {
            Console.WriteLine("No executions yet.");
            return;
        }

        Console.WriteLine($"{"Task",-12} {"Decision",-12} {"Duration",8} {"Cost",8} {"Files",6}");
        Console.WriteLine(new string('-', 50));

        foreach (var r in _history)
        {
            Console.WriteLine($"{r.TaskId,-12} {r.Decision,-12} {r.Duration.TotalSeconds,6:F1}s  ${r.EstimatedCost:F2}  {r.FilesChanged}");
        }
    }

    private void ExplainTask(string taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            Console.WriteLine("Usage: explain <task-id>");
            return;
        }

        var record = _history.FirstOrDefault(r =>
            r.TaskId.Equals(taskId, StringComparison.OrdinalIgnoreCase));

        if (record is null)
        {
            Console.WriteLine($"No execution found for task '{taskId}'.");
            return;
        }

        Console.WriteLine($"Task: {record.Objective}");
        Console.WriteLine($"Risk: {record.Risk}");
        Console.WriteLine($"Model: {record.Model}");
        Console.WriteLine($"Decision: {record.Decision}");
        Console.WriteLine($"Reason: {record.DecisionReason}");
        Console.WriteLine($"Duration: {record.Duration.TotalSeconds:F1}s");
        Console.WriteLine($"Tokens: {record.InputTokens} in / {record.OutputTokens} out");
        Console.WriteLine($"Cost: ${record.EstimatedCost:F2}");
        Console.WriteLine($"Files changed: {record.FilesChanged}");
        Console.WriteLine(
            $"Budget: {record.BudgetUsage.AttemptsUsed}/{record.BudgetUsage.MaximumAttempts} attempts, " +
            $"{record.BudgetUsage.WallClockElapsed.TotalSeconds:F1}/{record.BudgetUsage.WallClockLimitSeconds}s");
        if (record.AgentAttempts.Count > 0)
        {
            Console.WriteLine("Agent attempts:");
            foreach (var attempt in record.AgentAttempts)
            {
                Console.WriteLine(
                    $"  #{attempt.AttemptNumber} {attempt.FailureKind} " +
                    $"retry={attempt.WillRetry} — {attempt.DecisionReason}");
            }
        }
        if (record.AcceptanceCriteria.Count > 0)
        {
            Console.WriteLine("Acceptance evidence:");
            foreach (var criterion in record.AcceptanceCriteria)
            {
                var reference = string.IsNullOrWhiteSpace(criterion.EvidenceReference)
                    ? "missing"
                    : $"{criterion.EvidenceType}:{criterion.EvidenceReference}";
                Console.WriteLine(
                    $"  {criterion.CriterionId} {criterion.Status} {reference} — {criterion.Description}");
            }
        }
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

    private void ShowContext(string taskId)
    {
        if (string.IsNullOrEmpty(taskId))
        {
            Console.WriteLine("Usage: context <task-id>");
            return;
        }

        var samplePath = Path.Combine(_repoPath, "src");
        if (!Directory.Exists(samplePath))
        {
            Console.WriteLine($"Source path not found: {samplePath}");
            return;
        }

        var indexer = new CodebaseIndexer();
        var index = indexer.Index(samplePath);

        var selector = new ContextSelector();
        var package = selector.Select(index, taskId, taskId, ["src/**"]);

        var compiler = new ContextCompiler();
        var prompt = compiler.Compile(package, samplePath, taskId);

        Console.WriteLine($"Context Package: {package.Id}");
        Console.WriteLine($"Strategy: {package.Strategy}");
        Console.WriteLine($"Estimated tokens: {package.EstimatedTokens}");
        Console.WriteLine();
        Console.WriteLine("Compiled Prompt Preview:");
        Console.WriteLine(new string('-', 50));

        // Mostrar apenas as primeiras linhas do prompt
        var lines = prompt.Split('\n').Take(30).ToArray();
        foreach (var line in lines)
        {
            Console.WriteLine(line);
        }

        if (prompt.Split('\n').Length > 30)
        {
            Console.WriteLine($"\n... [{prompt.Split('\n').Length - 30} more lines]");
        }
    }

    private static string BuildLegacyPrompt(TaskContract contract, Dictionary<string, string> codeContext)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Task: {contract.Objective}");
        sb.AppendLine();
        sb.AppendLine("Code Context:");
        foreach (var kv in codeContext)
        {
            sb.AppendLine($"// File: {kv.Key}");
            sb.AppendLine(kv.Value);
            sb.AppendLine();
        }
        return sb.ToString();
    }
}
