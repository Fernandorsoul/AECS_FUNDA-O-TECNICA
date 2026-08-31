using System.Security.Cryptography;
using AECS.Application;
using AECS.Application.Classification;
using AECS.Application.ControlKernel;
using AECS.Application.Experiments;
using AECS.Application.EvidenceGraph;
using AECS.Application.Parsing;
using AECS.Application.Promotion;
using AECS.Application.Replay;
using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Cli;
using AECS.Cli.Jarvis;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using AECS.Infrastructure.Cryptography;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;
using AECS.Infrastructure.Sandbox;

// Load .env file if present
LoadEnvFile();

// Determine command
var command = args.Length > 0 ? args[0] : "jarvis";

if (command == "experiment")
    return await RunExperiment(args[1..]);
else if (command == "jarvis")
    return await RunJarvis(args[1..]);
else if (command == "promote")
    return await RunPromotion(args[1..]);
else if (command == "export-patch")
    return await RunPatchExport(args[1..]);
else if (command == "evidence-key")
    return RunEvidenceKey(args[1..]);
else if (command == "evidence")
    return await RunEvidenceQuery(args[1..]);
else if (command == "replay")
    return await RunReplay(args[1..]);
else
    return await RunSingle(args);

static async Task<int> RunEvidenceQuery(string[] args)
{
    const string usage =
        "Usage: aecs evidence <show|list|trace> --repo <path> " +
        "[--evidence <id>] [--task <id>] [--run <id>] [--candidate <id>] " +
        "[--baseline <commit>] [--decision <decision>] [--promotion <id>] " +
        "[--limit <1-500>] [--format <text|json|dot>] " + EvidenceStoreSelection.Usage;
    if (args.Length == 0 || args[0] is not ("show" or "list" or "trace"))
    {
        Console.WriteLine(usage);
        return 1;
    }

    var operation = args[0];
    string? repositoryPath = null;
    string? evidenceIdValue = null;
    string? taskId = null;
    string? runIdValue = null;
    string? candidateIdValue = null;
    string? baselineCommit = null;
    string? decisionValue = null;
    string? promotionIdValue = null;
    var limit = 100;
    var format = "text";
    var evidenceStoreSelection = new EvidenceStoreSelection();
    for (var index = 1; index < args.Length; index++)
    {
        if (args[index] == "--repo" && index + 1 < args.Length)
            repositoryPath = args[++index];
        else if (args[index] == "--evidence" && index + 1 < args.Length)
            evidenceIdValue = args[++index];
        else if (args[index] == "--task" && index + 1 < args.Length)
            taskId = args[++index];
        else if (args[index] == "--run" && index + 1 < args.Length)
            runIdValue = args[++index];
        else if (args[index] == "--candidate" && index + 1 < args.Length)
            candidateIdValue = args[++index];
        else if (args[index] == "--baseline" && index + 1 < args.Length)
            baselineCommit = args[++index];
        else if (args[index] == "--decision" && index + 1 < args.Length)
            decisionValue = args[++index];
        else if (args[index] == "--promotion" && index + 1 < args.Length)
            promotionIdValue = args[++index];
        else if (args[index] == "--limit" && index + 1 < args.Length &&
                 int.TryParse(args[++index], out var parsedLimit))
            limit = parsedLimit;
        else if (args[index] == "--format" && index + 1 < args.Length)
            format = args[++index];
        else if (evidenceStoreSelection.TryConsume(args, ref index))
        {
        }
        else
        {
            Console.WriteLine(usage);
            return 1;
        }
    }

    var needsEvidenceId = operation is "show" or "trace";
    if (repositoryPath is null ||
        (needsEvidenceId && !Guid.TryParse(evidenceIdValue, out _)) ||
        (runIdValue is not null && !Guid.TryParse(runIdValue, out _)) ||
        (candidateIdValue is not null && !Guid.TryParse(candidateIdValue, out _)) ||
        (promotionIdValue is not null && !Guid.TryParse(promotionIdValue, out _)) ||
        (decisionValue is not null &&
         !Enum.TryParse<TaskDecision>(decisionValue, ignoreCase: true, out _)) ||
        format is not ("text" or "json" or "dot") ||
        (operation == "list" && format == "dot"))
    {
        Console.WriteLine(usage);
        return 1;
    }

    if (!TryCreateEvidenceStore(evidenceStoreSelection, out var store))
        return 1;
    if (store is not IEvidenceGraphSource graphSource)
    {
        Console.WriteLine("ERROR: selected evidence store does not support graph queries.");
        return 1;
    }

    var scope = new EvidenceReadScope
    {
        RepositoryPath = repositoryPath,
        Principal = Environment.UserName
    };
    var service = new EvidenceGraphService(graphSource);
    try
    {
        if (operation == "list")
        {
            var queryResult = await service.ListAsync(new EvidenceGraphQuery
            {
                TaskId = taskId,
                RunId = runIdValue is null ? null : Guid.Parse(runIdValue),
                CandidateId = candidateIdValue is null ? null : Guid.Parse(candidateIdValue),
                BaselineCommit = baselineCommit,
                Decision = decisionValue is null
                    ? null
                    : Enum.Parse<TaskDecision>(decisionValue, ignoreCase: true),
                PromotionId = promotionIdValue is null ? null : Guid.Parse(promotionIdValue),
                Limit = limit
            }, scope, CancellationToken.None);
            Console.Write(format == "json"
                ? EvidenceGraphFormatter.ToJson(queryResult) + Environment.NewLine
                : EvidenceGraphFormatter.ToListText(queryResult));
            return 0;
        }

        var evidenceId = Guid.Parse(evidenceIdValue!);
        var graph = operation == "show"
            ? await service.ShowAsync(evidenceId, scope, CancellationToken.None)
            : await service.TraceAsync(evidenceId, scope, CancellationToken.None);
        if (graph is null)
        {
            Console.WriteLine($"Evidence '{evidenceId:N}' was not found.");
            return 1;
        }

        Console.Write(format switch
        {
            "json" => EvidenceGraphFormatter.ToJson(graph) + Environment.NewLine,
            "dot" => EvidenceGraphFormatter.ToDot(graph),
            _ when operation == "show" => EvidenceGraphFormatter.ToShowText(graph),
            _ => EvidenceGraphFormatter.ToTraceText(graph)
        });
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: evidence query failed closed: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunReplay(string[] args)
{
    string? repositoryPath = null;
    string? evidenceIdValue = null;
    var allowHostExecution = false;
    var evidenceStoreSelection = new EvidenceStoreSelection();

    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--repo" && index + 1 < args.Length)
            repositoryPath = args[++index];
        else if (args[index] == "--evidence" && index + 1 < args.Length)
            evidenceIdValue = args[++index];
        else if (args[index] == "--allow-host-execution")
            allowHostExecution = true;
        else if (evidenceStoreSelection.TryConsume(args, ref index))
        {
        }
        else
        {
            Console.WriteLine(
                "Usage: aecs replay --repo <path> --evidence <id> " +
                "[--allow-host-execution] " +
                EvidenceStoreSelection.Usage);
            return 1;
        }
    }

    if (repositoryPath is null || !Guid.TryParse(evidenceIdValue, out var evidenceId))
    {
        Console.WriteLine(
            "Usage: aecs replay --repo <path> --evidence <id> " +
            "[--allow-host-execution] " +
            EvidenceStoreSelection.Usage);
        return 1;
    }

    if (!TryCreateEvidenceStore(evidenceStoreSelection, out var store))
        return 1;

    try
    {
        var result = await new ExecutionReplayService(
            new SystemProcessRunner(),
            store,
            new DockerStagedProcessRunnerFactory(
                new SystemProcessRunner(),
                allowHostExecution))
            .ReplayAsync(new ExecutionReplayRequest
            {
                EvidenceId = evidenceId,
                RepositoryPath = repositoryPath
            }, CancellationToken.None);
        Console.WriteLine("AECS EVIDENCE REPLAY");
        Console.WriteLine($"Outcome: {result.Outcome}");
        Console.WriteLine($"Message: {result.Message}");
        Console.WriteLine($"Replay evidence: {result.Evidence.Id:N}");
        Console.WriteLine($"Expected diff: {result.Evidence.ExpectedDiffHash}");
        Console.WriteLine($"Actual diff: {result.Evidence.ActualDiffHash}");
        foreach (var tool in result.Evidence.Tools)
            Console.WriteLine($"Tool {tool.Tool}: {tool.Status}");
        foreach (var gate in result.Evidence.Gates)
            Console.WriteLine($"Gate {gate.Gate}: {gate.Status}");
        return result.Succeeded ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: evidence replay failed closed: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunPromotion(string[] args)
{
    string? repositoryPath = null;
    string? evidenceIdValue = null;
    string? expectedDiffHash = null;
    string? actor = null;
    string? policyReference = null;
    string? humanApprovalReference = null;
    var evidenceStoreSelection = new EvidenceStoreSelection();
    var userConfirmed = false;

    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--repo" && index + 1 < args.Length)
            repositoryPath = args[++index];
        else if (args[index] == "--evidence" && index + 1 < args.Length)
            evidenceIdValue = args[++index];
        else if (args[index] == "--diff-hash" && index + 1 < args.Length)
            expectedDiffHash = args[++index];
        else if (args[index] == "--actor" && index + 1 < args.Length)
            actor = args[++index];
        else if (args[index] == "--policy" && index + 1 < args.Length)
            policyReference = args[++index];
        else if (args[index] == "--human-approval" && index + 1 < args.Length)
            humanApprovalReference = args[++index];
        else if (args[index] == "--confirm")
            userConfirmed = true;
        else if (evidenceStoreSelection.TryConsume(args, ref index))
        {
        }
    }

    var confirmationCount = (userConfirmed ? 1 : 0) +
        (policyReference is null ? 0 : 1) +
        (humanApprovalReference is null ? 0 : 1);
    if (repositoryPath is null ||
        !Guid.TryParse(evidenceIdValue, out var evidenceId) ||
        string.IsNullOrWhiteSpace(expectedDiffHash) ||
        string.IsNullOrWhiteSpace(actor) ||
        confirmationCount != 1)
    {
        Console.WriteLine(
            "Usage: aecs promote --repo <path> --evidence <id> --diff-hash <sha256> " +
            "--actor <actor> (--confirm | --policy <reference> | --human-approval <reference>) " +
            EvidenceStoreSelection.Usage);
        return 1;
    }

    var approval = userConfirmed
        ? new PromotionApproval { Kind = PromotionApprovalKind.UserConfirmation }
        : policyReference is not null
            ? new PromotionApproval
            {
                Kind = PromotionApprovalKind.Policy,
                Reference = policyReference
            }
            : new PromotionApproval
            {
                Kind = PromotionApprovalKind.HumanReview,
                Reference = humanApprovalReference!
            };
    if (!TryCreateEvidenceStore(evidenceStoreSelection, out var store))
        return 1;
    var service = new CandidatePromotionService(new SystemProcessRunner(), store);
    CandidatePromotionResult result;
    try
    {
        result = await service.PromoteAsync(new CandidatePromotionRequest
        {
            EvidenceId = evidenceId,
            RepositoryPath = repositoryPath,
            ExpectedDiffHash = expectedDiffHash,
            Actor = actor,
            Approval = approval
        }, CancellationToken.None);
    }
    catch (Exception)
    {
        Console.WriteLine(
            "ERROR: evidence store operation failed; no backend fallback was attempted.");
        return 1;
    }

    PrintPromotionResult(result);
    return result.Succeeded ? 0 : 1;
}

static async Task<int> RunPatchExport(string[] args)
{
    string? evidenceIdValue = null;
    string? expectedDiffHash = null;
    string? outputPath = null;
    string? actor = null;
    var evidenceStoreSelection = new EvidenceStoreSelection();

    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--evidence" && index + 1 < args.Length)
            evidenceIdValue = args[++index];
        else if (args[index] == "--diff-hash" && index + 1 < args.Length)
            expectedDiffHash = args[++index];
        else if (args[index] == "--output" && index + 1 < args.Length)
            outputPath = args[++index];
        else if (args[index] == "--actor" && index + 1 < args.Length)
            actor = args[++index];
        else if (evidenceStoreSelection.TryConsume(args, ref index))
        {
        }
    }

    if (!Guid.TryParse(evidenceIdValue, out var evidenceId) ||
        string.IsNullOrWhiteSpace(expectedDiffHash) ||
        string.IsNullOrWhiteSpace(outputPath) ||
        string.IsNullOrWhiteSpace(actor))
    {
        Console.WriteLine(
            "Usage: aecs export-patch --evidence <id> --diff-hash <sha256> " +
            "--output <path> --actor <actor> " + EvidenceStoreSelection.Usage);
        return 1;
    }

    if (!TryCreateEvidenceStore(evidenceStoreSelection, out var store))
        return 1;
    var service = new CandidatePromotionService(new SystemProcessRunner(), store);
    CandidatePromotionResult result;
    try
    {
        result = await service.ExportPatchAsync(new CandidatePatchExportRequest
        {
            EvidenceId = evidenceId,
            DestinationPath = outputPath,
            ExpectedDiffHash = expectedDiffHash,
            Actor = actor
        }, CancellationToken.None);
    }
    catch (Exception)
    {
        Console.WriteLine(
            "ERROR: evidence store operation failed; no backend fallback was attempted.");
        return 1;
    }

    PrintPromotionResult(result);
    return result.Succeeded ? 0 : 1;
}

static void PrintPromotionResult(CandidatePromotionResult result)
{
    Console.WriteLine($"Status: {result.Status}");
    Console.WriteLine($"Message: {result.Message}");
    Console.WriteLine($"Promotion evidence: {result.Evidence.Id:N}");
    Console.WriteLine($"Diff hash: {result.Evidence.DiffHash}");
    if (!string.IsNullOrWhiteSpace(result.OutputPath))
        Console.WriteLine($"Output: {result.OutputPath}");
}

static bool TryCreateEvidenceStore(
    EvidenceStoreSelection selection,
    out IExecutionEvidenceStore store)
{
    try
    {
        store = selection.Create();
        return true;
    }
    catch (Exception ex)
    {
        store = null!;
        Console.WriteLine($"ERROR: evidence store configuration failed: {ex.Message}");
        return false;
    }
}

static int RunEvidenceKey(string[] args)
{
    if (args.Length == 0 || !string.Equals(args[0], "rotate", StringComparison.Ordinal))
    {
        Console.WriteLine("Usage: aecs evidence-key rotate [--key-directory <path>]");
        return 1;
    }

    string? keyDirectory = null;
    for (var index = 1; index < args.Length; index++)
    {
        if (args[index] == "--key-directory" && index + 1 < args.Length)
            keyDirectory = args[++index];
        else
        {
            Console.WriteLine("Usage: aecs evidence-key rotate [--key-directory <path>]");
            return 1;
        }
    }

    keyDirectory ??= JsonExecutionEvidenceStore.GetDefaultKeyDirectoryPath();
    try
    {
        var newKeyId = RsaEvidenceSignatureService.RotateKey(keyDirectory);
        Console.WriteLine($"Evidence signing key rotated: {newKeyId}");
        Console.WriteLine($"Trusted public keys retained in: {Path.GetFullPath(keyDirectory)}");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or CryptographicException)
    {
        Console.WriteLine($"ERROR: evidence key rotation failed: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunExperiment(string[] args)
{
    string? repoPath = null;
    string? tasksDir = null;
    bool useMock = false;
    bool allowHostExecution = false;
    string? cloudKey = null;
    string? cloudModel = null;
    string? cloudUrl = null;
    var evidenceStoreSelection = new EvidenceStoreSelection();

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--tasks" && i + 1 < args.Length)
            tasksDir = args[++i];
        else if (args[i] == "--mock")
            useMock = true;
        else if (args[i] == "--allow-host-execution")
            allowHostExecution = true;
        else if (args[i] == "--cloud-key" && i + 1 < args.Length)
            cloudKey = args[++i];
        else if (args[i] == "--cloud-model" && i + 1 < args.Length)
            cloudModel = args[++i];
        else if (args[i] == "--cloud-url" && i + 1 < args.Length)
            cloudUrl = args[++i];
        else if (evidenceStoreSelection.TryConsume(args, ref i))
        {
        }
    }

    if (repoPath is null || tasksDir is null)
    {
        Console.WriteLine(
            "Usage: aecs experiment --repo <path> --tasks <dir> [--mock] " +
            "[--allow-host-execution] " +
            "[--cloud-key <key>] [--cloud-model <model>] " +
            EvidenceStoreSelection.Usage);
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
    if (!TryCreateEvidenceStore(evidenceStoreSelection, out var evidenceStore))
        return 1;

    ExperimentReport report;
    try
    {
        var runner = new ExperimentRunner(CreatePipeline(
            agent,
            evidenceStore,
            allowHostExecution));
        report = await runner.RunAsync(repoPath, taskFiles, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: experiment failed closed: {ex.Message}");
        return 1;
    }

    Console.WriteLine(ExperimentReportFormatter.Format(report));

    return 0;
}

static async Task<int> RunSingle(string[] args)
{
    string? repoPath = null;
    string? taskFile = null;
    bool useMock = false;
    bool allowHostExecution = false;

    string? cloudKey = null;
    string? cloudModel = null;
    string? cloudUrl = null;
    var evidenceStoreSelection = new EvidenceStoreSelection();

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--task-file" && i + 1 < args.Length)
            taskFile = args[++i];
        else if (args[i] == "--mock")
            useMock = true;
        else if (args[i] == "--allow-host-execution")
            allowHostExecution = true;
        else if (args[i] == "--cloud-key" && i + 1 < args.Length)
            cloudKey = args[++i];
        else if (args[i] == "--cloud-model" && i + 1 < args.Length)
            cloudModel = args[++i];
        else if (args[i] == "--cloud-url" && i + 1 < args.Length)
            cloudUrl = args[++i];
        else if (evidenceStoreSelection.TryConsume(args, ref i))
        {
        }
    }

    if (repoPath is null || taskFile is null)
    {
        Console.WriteLine(
            "Usage: aecs run --repo <path> --task-file <path> [--mock] " +
            "[--allow-host-execution] " +
            "[--cloud-key <key>] [--cloud-model <model>] " + EvidenceStoreSelection.Usage);
        Console.WriteLine(
            "       aecs experiment --repo <path> --tasks <dir> [--mock] " +
            "[--allow-host-execution] " +
            "[--cloud-key <key>] [--cloud-model <model>] " + EvidenceStoreSelection.Usage);
        Console.WriteLine(
            "       aecs promote --repo <path> --evidence <id> --diff-hash <sha256> " +
            "--actor <actor> --confirm " + EvidenceStoreSelection.Usage);
        Console.WriteLine(
            "       aecs export-patch --evidence <id> --diff-hash <sha256> " +
            "--output <path> --actor <actor> " + EvidenceStoreSelection.Usage);
        Console.WriteLine(
            "       aecs replay --repo <path> --evidence <id> " +
            "[--allow-host-execution] " +
            EvidenceStoreSelection.Usage);
        Console.WriteLine(
            "       aecs evidence <show|list|trace> --repo <path> " +
            "[--evidence <id>] [--format <text|json|dot>] " +
            EvidenceStoreSelection.Usage);
        Console.WriteLine("       aecs evidence-key rotate [--key-directory <path>]");
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
    if (!TryCreateEvidenceStore(evidenceStoreSelection, out var evidenceStore))
        return 1;

    StagedExecutionResult execution;
    try
    {
        execution = await CreatePipeline(
            agent,
            evidenceStore,
            allowHostExecution).RunAsync(
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
    Console.WriteLine($"Context: {execution.ContextManifest.Id}");
    Console.WriteLine($"Context files: {execution.ContextManifest.Files.Count} " +
        $"({execution.ContextManifest.EstimatedTokens} estimated tokens)");
    Console.WriteLine($"Context hash: {execution.ContextManifest.ManifestHash}");
    Console.WriteLine($"Candidate: {execution.CandidateChangeSet.Id:N}");
    Console.WriteLine($"Diff hash: {execution.CandidateChangeSet.DiffHash}");
    Console.WriteLine("Changed files:");
    foreach (var file in execution.CandidateChangeSet.ChangedFiles)
        Console.WriteLine($"  {file}");
    if (execution.CandidateChangeSet.ChangedFiles.Count == 0)
        Console.WriteLine("  (none)");

    Console.WriteLine("Agent attempts:");
    foreach (var attempt in execution.AgentAttempts)
    {
        var retry = attempt.WillRetry
            ? $" retry in {attempt.RetryDelay?.TotalSeconds:F1}s"
            : string.Empty;
        Console.WriteLine(
            $"  #{attempt.AttemptNumber} {attempt.FailureKind} " +
            $"tokens={attempt.InputTokens + attempt.OutputTokens} cost=${attempt.EstimatedCost:F4}{retry} " +
            $"— {attempt.DecisionReason}");
    }
    if (execution.AgentAttempts.Count == 0)
        Console.WriteLine("  (agent was not called)");
    Console.WriteLine(
        $"Budget: {execution.BudgetUsage.AttemptsUsed}/{execution.BudgetUsage.MaximumAttempts} attempts, " +
        $"{execution.BudgetUsage.WallClockElapsed.TotalSeconds:F1}/{execution.BudgetUsage.WallClockLimitSeconds}s, " +
        $"{execution.BudgetUsage.InputTokens + execution.BudgetUsage.OutputTokens} tokens, " +
        $"${execution.BudgetUsage.EstimatedCost:F4}");

    Console.WriteLine("Verification:");
    foreach (var result in execution.VerificationResults)
        Console.WriteLine($"  {result.Verifier.PadRight(24)} {result.Status}");

    Console.WriteLine("Acceptance evidence:");
    foreach (var criterion in execution.AcceptanceCriteriaResults)
    {
        var reference = string.IsNullOrWhiteSpace(criterion.EvidenceReference)
            ? "missing"
            : $"{criterion.EvidenceType}:{criterion.EvidenceReference}";
        Console.WriteLine(
            $"  {criterion.CriterionId.PadRight(8)} {criterion.Status,-5} {reference} — {criterion.Description}");
    }
    if (execution.AcceptanceCriteriaResults.Count == 0)
        Console.WriteLine("  (none declared)");

    Console.WriteLine($"Decision: {execution.Decision.Decision.ToString().ToUpperInvariant()}");
    Console.WriteLine($"Reason: {execution.Decision.Reason}");
    Console.WriteLine($"Original repository unchanged: {execution.OriginalRepositoryUnchanged}");
    Console.WriteLine($"Evidence ID: {execution.EvidenceId:N}");
    Console.WriteLine($"Evidence: {execution.EvidenceLocation}");

    return 0;
}

static StagedExecutionPipeline CreatePipeline(
    IAgentAdapter agent,
    IExecutionEvidenceStore evidenceStore,
    bool allowHostExecution = false)
{
    var processRunner = new SystemProcessRunner();
    return new StagedExecutionPipeline(
        agent,
        processRunner,
        evidenceStore,
        stagedProcessRunnerFactory: new DockerStagedProcessRunnerFactory(
            processRunner,
            allowHostExecution));
}

static async Task<int> RunJarvis(string[] args)
{
    string? repoPath = null;
    bool useMock = false;
    bool allowHostExecution = false;
    var evidenceStoreSelection = new EvidenceStoreSelection();

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--mock")
            useMock = true;
        else if (args[i] == "--allow-host-execution")
            allowHostExecution = true;
        else if (evidenceStoreSelection.TryConsume(args, ref i))
        {
        }
    }

    repoPath ??= ".";

    if (!TryCreateEvidenceStore(evidenceStoreSelection, out var evidenceStore))
        return 1;

    var repl = new JarvisRepl(
        repoPath,
        useMock,
        evidenceStore,
        allowHostExecution);
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
