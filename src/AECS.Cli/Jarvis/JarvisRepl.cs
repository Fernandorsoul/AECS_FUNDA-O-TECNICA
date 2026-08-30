using AECS.Application;
using AECS.Application.Classification;
using AECS.Application.ContextCompiler;
using AECS.Application.ControlKernel;
using AECS.Application.Experiments;
using AECS.Application.Parsing;
using AECS.Application.Verification;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;

namespace AECS.Cli.Jarvis;

public class JarvisRepl
{
    private readonly string _repoPath;
    private readonly bool _useMock;
    private readonly TaskContractParser _parser = new();
    private readonly RiskClassifier _riskClassifier = new();
    private readonly ExecutionController _executionController = new();
    private readonly ControlKernel _kernel = new();
    private readonly DecisionEngine _decisionEngine = new();
    private readonly List<TaskExperimentResult> _history = [];
    private TaskExperimentResult? _lastResult;

    public JarvisRepl(string repoPath, bool useMock)
    {
        _repoPath = repoPath;
        _useMock = useMock;
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
        var risk = _riskClassifier.Classify(contract);

        contract = new TaskContract
        {
            Id = contract.Id,
            Objective = contract.Objective,
            AcceptanceCriteria = contract.AcceptanceCriteria,
            Scope = contract.Scope,
            Constraints = new TaskConstraints
            {
                SecurityRisk = risk,
                DatabaseMigration = contract.Constraints.DatabaseMigration,
                ExternalDependency = contract.Constraints.ExternalDependency
            },
            Budget = contract.Budget,
            Verification = contract.Verification,
            Approval = contract.Approval,
            Status = contract.Status,
            CreatedAt = contract.CreatedAt
        };

        // Context Compiler: Index and select relevant files
        var sourcePath = Path.Combine(_repoPath, "src");
        var testPath = Path.Combine(_repoPath, "tests");
        CodebaseIndex? index = null;
        
        if (Directory.Exists(sourcePath))
        {
            var indexer = new CodebaseIndexer();
            index = indexer.Index(sourcePath);
            
            // Merge test files if available
            if (Directory.Exists(testPath))
            {
                var testIndex = indexer.Index(testPath);
                index = new CodebaseIndex
                {
                    RootPath = index.RootPath,
                    SourceFiles = index.SourceFiles,
                    TestFiles = testIndex.TestFiles,
                    Symbols = index.Symbols.Concat(testIndex.Symbols).ToList(),
                    Dependencies = index.Dependencies.Concat(testIndex.Dependencies)
                        .ToDictionary(kv => kv.Key, kv => kv.Value)
                };
            }
        }

        ContextPackage? contextPackage = null;
        if (index != null)
        {
            var selector = new ContextSelector();
            contextPackage = selector.Select(index, contract.Id, contract.Objective, contract.Scope.Allowed);
        }

        var plan = await _executionController.PlanAsync(contract, ct);

        Console.WriteLine($"Task: {contract.Objective}");
        Console.WriteLine($"Risk: {risk}");
        Console.WriteLine($"Model: {plan.Model}");
        if (contextPackage != null)
        {
            Console.WriteLine($"Context: {contextPackage.SelectedFiles.Count} files, ~{contextPackage.EstimatedTokens} tokens");
        }
        Console.WriteLine();

        IAgentAdapter agent = _useMock
            ? new MockAgentAdapter()
            : new OllamaAdapter(new HttpClient());

        // Build CodeContext from selected files
        var codeContext = new Dictionary<string, string>();
        if (contextPackage != null && index != null)
        {
            foreach (var filePath in contextPackage.SelectedFiles)
            {
                var fullPath = Path.Combine(index.RootPath, filePath);
                if (File.Exists(fullPath))
                {
                    var content = await File.ReadAllTextAsync(fullPath, ct);
                    codeContext[filePath] = content;
                }
            }
        }

        var request = new AgentExecutionRequest
        {
            TaskId = contract.Id,
            Objective = contract.Objective,
            AcceptanceCriteria = contract.AcceptanceCriteria,
            RepoPath = _repoPath,
            Scope = contract.Scope,
            Budget = contract.Budget,
            Risk = risk,
            Model = plan.Model,
            CodeContext = codeContext
        };

        var agentResult = await agent.ExecuteAsync(request, ct);

        // Apply model changes to workspace
        if (agentResult.Success && !string.IsNullOrEmpty(agentResult.StdOut))
        {
            var fileApplicator = new FileApplicator();
            var applyResult = fileApplicator.ApplyChanges(agentResult.StdOut, _repoPath);
            if (applyResult.AppliedChanges.Count > 0)
            {
                agentResult = new AgentRunResult
                {
                    Success = agentResult.Success,
                    StdOut = agentResult.StdOut,
                    StdErr = agentResult.StdErr,
                    ExitCode = agentResult.ExitCode,
                    Duration = agentResult.Duration,
                    InputTokens = agentResult.InputTokens,
                    OutputTokens = agentResult.OutputTokens,
                    EstimatedCost = agentResult.EstimatedCost,
                    FilesChanged = applyResult.AppliedChanges.Select(c => c.FilePath).ToList(),
                    ExitReason = agentResult.ExitReason
                };
                Console.WriteLine($"  [AECS] Applied {applyResult.AppliedChanges.Count} file change(s) to workspace");
            }
        }

        var kernelDecision = _kernel.ValidateExecution(contract, agentResult);

        TaskDecision decision;
        string reason;

        if (!kernelDecision.Allowed)
        {
            decision = TaskDecision.Rejected;
            reason = kernelDecision.Reason;
        }
        else
        {
            var verificationContext = new VerificationContext
            {
                TaskId = contract.Id,
                AgentRunId = Guid.NewGuid().ToString(),
                RepoPath = _repoPath,
                Contract = contract,
                AgentResult = agentResult
            };

            var verifiers = new List<IVerifier>
            {
                new BuildVerifier(),
                new TestVerifier(),
                new ScopeVerifier(),
                new BudgetVerifier(),
                new EB001Verifier(),
                new EB002Verifier(),
                new EB003Verifier(),
                new EB004Verifier(),
                new EB005Verifier()
            };

            var results = new List<VerificationResult>();
            foreach (var v in verifiers)
                results.Add(await v.VerifyAsync(verificationContext, ct));

            var d = _decisionEngine.Decide(results, contract);
            decision = d.Decision;
            reason = d.Reason;
        }

        var result = new TaskExperimentResult
        {
            TaskId = contract.Id,
            Objective = contract.Objective,
            Risk = risk,
            Model = plan.Model,
            Decision = decision,
            DecisionReason = reason,
            Duration = agentResult.Duration,
            InputTokens = agentResult.InputTokens,
            OutputTokens = agentResult.OutputTokens,
            EstimatedCost = agentResult.EstimatedCost,
            FilesChanged = agentResult.FilesChanged.Count
        };

        _history.Add(result);
        _lastResult = result;

        Console.WriteLine($"Decision: {decision}");
        Console.WriteLine($"Duration: {agentResult.Duration.TotalSeconds:F1}s");
        Console.WriteLine($"Cost: ${agentResult.EstimatedCost:F2}");
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

        var runner = new ExperimentRunner(agent);
        var report = await runner.RunAsync(_repoPath, taskFiles, ct);

        Console.WriteLine(ExperimentReportFormatter.Format(report));

        _history.AddRange(report.Results);
        if (report.Results.Count > 0)
            _lastResult = report.Results.Last();
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

        Console.WriteLine($"Context Package: {package.Id}");
        Console.WriteLine($"Strategy: {package.Strategy}");
        Console.WriteLine($"Estimated tokens: {package.EstimatedTokens}");
        Console.WriteLine();
        Console.WriteLine("Selected files:");
        foreach (var f in package.SelectedFiles)
            Console.WriteLine($"  {f}");
        Console.WriteLine();
        Console.WriteLine("Symbols:");
        foreach (var s in package.SelectedSymbols)
            Console.WriteLine($"  {s}");
    }
}
