using AECS.Application;
using AECS.Application.Classification;
using AECS.Application.ControlKernel;
using AECS.Application.Experiments;
using AECS.Application.Parsing;
using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Cli.Jarvis;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;

// Load .env file if present
LoadEnvFile();

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
    string? cloudKey = null;
    string? cloudModel = null;
    string? cloudUrl = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--tasks" && i + 1 < args.Length)
            tasksDir = args[++i];
        else if (args[i] == "--mock")
            useMock = true;
        else if (args[i] == "--cloud-key" && i + 1 < args.Length)
            cloudKey = args[++i];
        else if (args[i] == "--cloud-model" && i + 1 < args.Length)
            cloudModel = args[++i];
        else if (args[i] == "--cloud-url" && i + 1 < args.Length)
            cloudUrl = args[++i];
    }

    if (repoPath is null || tasksDir is null)
    {
        Console.WriteLine("Usage: aecs experiment --repo <path> --tasks <dir> [--mock] [--cloud-key <key>] [--cloud-model <model>]");
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

    IAgentAdapter agent = BuildAgent(useMock, cloudKey, cloudModel, cloudUrl);

    var runner = new ExperimentRunner(CreatePipeline(agent));
    var report = await runner.RunAsync(repoPath, taskFiles, CancellationToken.None);

    Console.WriteLine(ExperimentReportFormatter.Format(report));

    return 0;
}

static async Task<int> RunSingle(string[] args)
{
    string? repoPath = null;
    string? taskFile = null;
    bool useMock = false;

    string? cloudKey = null;
    string? cloudModel = null;
    string? cloudUrl = null;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--task-file" && i + 1 < args.Length)
            taskFile = args[++i];
        else if (args[i] == "--mock")
            useMock = true;
        else if (args[i] == "--cloud-key" && i + 1 < args.Length)
            cloudKey = args[++i];
        else if (args[i] == "--cloud-model" && i + 1 < args.Length)
            cloudModel = args[++i];
        else if (args[i] == "--cloud-url" && i + 1 < args.Length)
            cloudUrl = args[++i];
    }

    if (repoPath is null || taskFile is null)
    {
        Console.WriteLine("Usage: aecs run --repo <path> --task-file <path> [--mock] [--cloud-key <key>] [--cloud-model <model>]");
        Console.WriteLine("       aecs experiment --repo <path> --tasks <dir> [--mock] [--cloud-key <key>] [--cloud-model <model>]");
        return 1;
    }

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

    IAgentAdapter agent = BuildAgent(useMock, cloudKey, cloudModel, cloudUrl);

    StagedExecutionResult execution;
    try
    {
        execution = await CreatePipeline(agent).RunAsync(
            repoPath,
            contract,
            CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: staged execution failed closed: {ex.Message}");
        return 1;
    }

    Console.WriteLine("AECS STAGED RUN");
    Console.WriteLine($"Task: {execution.Contract.Objective}");
    Console.WriteLine($"Risk: {execution.Risk}");
    Console.WriteLine($"Baseline: {execution.Baseline.Commit} ({execution.Baseline.Branch})");
    Console.WriteLine("Baseline verification:");
    foreach (var result in execution.BaselineVerificationResults)
        Console.WriteLine($"  {result.Verifier.PadRight(24)} {result.Status}");
    if (execution.BaselineVerificationResults.Count == 0)
        Console.WriteLine("  (not required)");
    Console.WriteLine($"Candidate: {execution.CandidateChangeSet.Id:N}");
    Console.WriteLine($"Diff hash: {execution.CandidateChangeSet.DiffHash}");
    Console.WriteLine("Changed files:");
    foreach (var file in execution.CandidateChangeSet.ChangedFiles)
        Console.WriteLine($"  {file}");
    if (execution.CandidateChangeSet.ChangedFiles.Count == 0)
        Console.WriteLine("  (none)");

    Console.WriteLine("Verification:");
    foreach (var result in execution.VerificationResults)
        Console.WriteLine($"  {result.Verifier.PadRight(24)} {result.Status}");

    Console.WriteLine($"Decision: {execution.Decision.Decision.ToString().ToUpperInvariant()}");
    Console.WriteLine($"Reason: {execution.Decision.Reason}");
    Console.WriteLine($"Original repository unchanged: {execution.OriginalRepositoryUnchanged}");
    Console.WriteLine($"Evidence ID: {execution.EvidenceId:N}");
    Console.WriteLine($"Evidence: {execution.EvidenceLocation}");

    return 0;
}

static StagedExecutionPipeline CreatePipeline(IAgentAdapter agent)
{
    var processRunner = new SystemProcessRunner();
    var evidenceStore = new JsonExecutionEvidenceStore(
        JsonExecutionEvidenceStore.GetDefaultRootPath());
    return new StagedExecutionPipeline(agent, processRunner, evidenceStore);
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

static IAgentAdapter BuildAgent(bool useMock, string? cloudKey, string? cloudModel, string? cloudUrl)
{
    if (useMock)
        return new MockAgentAdapter();

    var localAdapter = new OllamaAdapter(new HttpClient());

    // Read from CLI args first, then environment variables
    var key = cloudKey
        ?? Environment.GetEnvironmentVariable("OPENAI_API_KEY")
        ?? Environment.GetEnvironmentVariable("ANTHROPIC_API_KEY");

    var model = cloudModel
        ?? Environment.GetEnvironmentVariable("OPENAI_MODEL")
        ?? Environment.GetEnvironmentVariable("ANTHROPIC_MODEL")
        ?? "gpt-4o-mini";

    var url = cloudUrl
        ?? Environment.GetEnvironmentVariable("OPENAI_BASE_URL")
        ?? Environment.GetEnvironmentVariable("ANTHROPIC_BASE_URL")
        ?? "https://api.openai.com/v1";

    if (string.IsNullOrEmpty(key))
        return localAdapter;

    // Cloud fallback configured — wrap with FallbackAdapter
    Console.WriteLine($"  [AECS] Cloud fallback enabled: {model}");

    var cloudOptions = new CloudAdapterOptions
    {
        ApiKey = key,
        Model = model,
        BaseUrl = url
    };

    var cloudAdapter = new CloudAdapter(new HttpClient(), cloudOptions);
    return new FallbackAdapter(localAdapter, cloudAdapter);
}

static void LoadEnvFile()
{
    // Search for .env starting from current directory and walking up
    var dir = Directory.GetCurrentDirectory();
    while (dir is not null)
    {
        var envPath = Path.Combine(dir, ".env");
        if (File.Exists(envPath))
        {
            LoadEnvFromFile(envPath);
            return;
        }
        dir = Directory.GetParent(dir)?.FullName;
    }
}

static void LoadEnvFromFile(string path)
{
    foreach (var line in File.ReadAllLines(path))
    {
        var trimmed = line.Trim();
        if (string.IsNullOrEmpty(trimmed) || trimmed.StartsWith('#'))
            continue;

        var separatorIndex = trimmed.IndexOf('=');
        if (separatorIndex <= 0)
            continue;

        var key = trimmed[..separatorIndex].Trim();
        var value = trimmed[(separatorIndex + 1)..].Trim();

        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value))
            Environment.SetEnvironmentVariable(key, value);
    }
}
