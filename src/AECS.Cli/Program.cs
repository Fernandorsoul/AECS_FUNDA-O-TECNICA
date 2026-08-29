using AECS.Application;
using AECS.Application.Classification;
using AECS.Application.ControlKernel;
using AECS.Application.Experiments;
using AECS.Application.Parsing;
using AECS.Application.Verification;
using AECS.Cli.Jarvis;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;

// Determine command
var command = args.Length > 0 ? args[0] : "jarvis";

if (command == "experiment")
    return await RunExperiment(args[1..]);
else if (command == "jarvis")
    return await RunJarvis(args[1..]);
else
    return await RunSingle(args);

static async Task<int> RunExperiment(string[] args)
{
    string? repoPath = null;
    string? tasksDir = null;
    bool useMock = false;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--tasks" && i + 1 < args.Length)
            tasksDir = args[++i];
        else if (args[i] == "--mock")
            useMock = true;
    }

    if (repoPath is null || tasksDir is null)
    {
        Console.WriteLine("Usage: aecs experiment --repo <path> --tasks <dir> [--mock]");
        return 1;
    }

    var taskFiles = Directory.GetFiles(tasksDir, "*.yaml")
        .Concat(Directory.GetFiles(tasksDir, "*.yml"))
        .OrderBy(f => f)
        .ToList();

    if (taskFiles.Count == 0)
    {
        Console.WriteLine($"No YAML task files found in {tasksDir}");
        return 1;
    }

    IAgentAdapter agent = useMock
        ? new MockAgentAdapter()
        : new OllamaAdapter(new HttpClient());

    var runner = new ExperimentRunner(agent);
    var report = await runner.RunAsync(repoPath, taskFiles, CancellationToken.None);

    Console.WriteLine(ExperimentReportFormatter.Format(report));

    return 0;
}

static async Task<int> RunSingle(string[] args)
{
    string? repoPath = null;
    string? taskFile = null;
    bool useMock = false;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--task-file" && i + 1 < args.Length)
            taskFile = args[++i];
        else if (args[i] == "--mock")
            useMock = true;
    }

    if (repoPath is null || taskFile is null)
    {
        Console.WriteLine("Usage: aecs run --repo <path> --task-file <path> [--mock]");
        Console.WriteLine("       aecs experiment --repo <path> --tasks <dir> [--mock]");
        return 1;
    }

    // 1. Load TaskContract
    TaskContract contract;
    try
    {
        var parser = new TaskContractParser();
        contract = parser.ParseFromFile(taskFile);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: Failed to parse task file: {ex.Message}");
        return 1;
    }

    // 2. Classify risk
    var riskClassifier = new RiskClassifier();
    var risk = riskClassifier.Classify(contract);

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

    // 3. Generate ExecutionPlan
    var executionController = new ExecutionController();
    var plan = await executionController.PlanAsync(contract, CancellationToken.None);

    // Print header
    Console.WriteLine("AECS RUN");
    Console.WriteLine();
    Console.WriteLine($"Task:");
    Console.WriteLine(contract.Objective);
    Console.WriteLine();
    Console.WriteLine($"Risk:");
    Console.WriteLine(risk.ToString());
    Console.WriteLine();
    Console.WriteLine("Allowed Scope:");
    foreach (var path in contract.Scope.Allowed)
        Console.WriteLine(path);
    Console.WriteLine();
    Console.WriteLine("Budget:");
    Console.WriteLine($"${contract.Budget.MaxCostUsd:F2}");
    Console.WriteLine($"{contract.Budget.MaxDurationSeconds} seconds");
    Console.WriteLine($"{contract.Budget.MaxRetries} retry");
    Console.WriteLine();

    // 4. Execute agent
    Console.WriteLine("Execution:");

    IAgentAdapter agent = useMock
        ? new MockAgentAdapter()
        : new OllamaAdapter(new HttpClient());

    var agentRequest = new AgentExecutionRequest
    {
        TaskId = contract.Id,
        Objective = contract.Objective,
        AcceptanceCriteria = contract.AcceptanceCriteria,
        RepoPath = repoPath,
        Scope = contract.Scope,
        Budget = contract.Budget,
        Risk = risk,
        Model = plan.Model
    };

    var agentResult = await agent.ExecuteAsync(agentRequest, CancellationToken.None);

    Console.WriteLine(agentResult.Success ? "SUCCESS" : "FAILED");
    Console.WriteLine();

    // 5. Files changed
    Console.WriteLine("Files changed:");
    if (agentResult.FilesChanged.Count > 0)
    {
        foreach (var file in agentResult.FilesChanged)
            Console.WriteLine(file);
    }
    else
    {
        Console.WriteLine("(none)");
    }
    Console.WriteLine();

    // 6. Control Kernel validation
    var kernel = new ControlKernel();
    var kernelDecision = kernel.ValidateExecution(contract, agentResult);

    if (!kernelDecision.Allowed)
    {
        Console.WriteLine("Verification:");
        Console.WriteLine($"  {kernelDecision.TargetState}: {kernelDecision.Reason}");
        Console.WriteLine();
        Console.WriteLine("Decision:");
        Console.WriteLine();
        Console.WriteLine("REJECTED");
        Console.WriteLine();
        Console.WriteLine($"Duration: {agentResult.Duration.TotalSeconds:F1}s");
        Console.WriteLine($"Estimated AI cost: ${agentResult.EstimatedCost:F2}");
        return 0;
    }

    // 7. Run verifiers
    Console.WriteLine("Verification:");

    var verificationContext = new VerificationContext
    {
        TaskId = contract.Id,
        AgentRunId = Guid.NewGuid().ToString(),
        RepoPath = repoPath,
        Contract = contract,
        AgentResult = agentResult
    };

    var verifiers = new List<IVerifier>
    {
        new BuildVerifier(),
        new TestVerifier(),
        new ScopeVerifier(),
        new BudgetVerifier()
    };

    var verificationResults = new List<VerificationResult>();
    foreach (var verifier in verifiers)
    {
        var result = await verifier.VerifyAsync(verificationContext, CancellationToken.None);
        verificationResults.Add(result);

        var status = result.Status == VerificationStatus.Pass ? "PASS" : "FAIL";
        var name = result.Verifier.PadRight(12);
        Console.WriteLine($"{name} {status}");
    }

    Console.WriteLine();

    // 8. Decision
    var decisionEngine = new DecisionEngine();
    var decision = decisionEngine.Decide(verificationResults, contract);

    Console.WriteLine("Decision:");
    Console.WriteLine();
    Console.WriteLine(decision.Decision.ToString().ToUpperInvariant());
    Console.WriteLine();

    Console.WriteLine($"Duration: {agentResult.Duration.TotalSeconds:F1}s");
    Console.WriteLine($"Estimated AI cost: ${agentResult.EstimatedCost:F2}");
    Console.WriteLine($"Evidence ID: {Guid.NewGuid():N}");

    return 0;
}

static async Task<int> RunJarvis(string[] args)
{
    string? repoPath = null;
    bool useMock = false;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--mock")
            useMock = true;
    }

    repoPath ??= ".";

    var repl = new JarvisRepl(repoPath, useMock);
    await repl.RunAsync(CancellationToken.None);
    return 0;
}
