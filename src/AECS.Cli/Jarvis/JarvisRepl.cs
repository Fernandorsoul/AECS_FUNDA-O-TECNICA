using AECS.Application;
using AECS.Application.Classification;
using AECS.Application.Experiments;
using AECS.Application.Jarvis;
using AECS.Application.Parsing;
using AECS.Application.Promotion;
using AECS.Cli.Runtime;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Cli.Jarvis;

public class JarvisRepl
{
    private readonly string _repoPath;
    private readonly AecsExecutionRuntime _runtime;
    private readonly TaskContractParser _parser = new();
    private readonly RiskClassifier _riskClassifier = new();
    private readonly DurableExecutionHistoryService? _durableHistory;
    private readonly CandidatePromotionService _promotion;

    public EffectiveAecsRuntimeConfiguration RuntimeConfiguration => _runtime.Configuration;

    public JarvisRepl(
        string repoPath,
        AecsExecutionRuntime runtime)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoPath);
        ArgumentNullException.ThrowIfNull(runtime);
        _repoPath = repoPath;
        _runtime = runtime;
        _promotion = runtime.CreatePromotionService();
        if (_runtime.EvidenceStore is IEvidenceGraphSource graphSource)
        {
            _durableHistory = new DurableExecutionHistoryService(
                _runtime.EvidenceStore,
                graphSource,
                repoPath,
                Environment.UserName);
        }
    }

    public async Task<int> RunAsync(CancellationToken cancellationToken)
    {
        var exitCode = 0;
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
                exitCode = 1;
                Console.WriteLine($"Error: {ex.Message}");
            }

            Console.WriteLine();
        }

        return exitCode;
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

            case "review":
                await ReviewCandidate(argument, ct);
                return false;

            case "export-patch":
                await ExportCandidatePatch(argument, ct);
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
        Console.WriteLine("  review <evidence>   Review, approve/reject and optionally promote a candidate");
        Console.WriteLine("  export-patch <evidence> <path>  Review and export without promotion");
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
        var execution = await _runtime.CreatePipeline().RunAsync(_repoPath, contract, ct);

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

        var runner = new ExperimentRunner(_runtime.CreatePipeline());
        var report = await runner.RunAsync(_repoPath, taskFiles, ct);

        Console.WriteLine(ExperimentReportFormatter.Format(report));

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

    private async Task ReviewCandidate(string argument, CancellationToken cancellationToken)
    {
        if (!TryParseReviewOptions(argument, out var options, out var error))
        {
            Console.WriteLine(
                "Usage: review <evidence-id> [--valid-minutes <1-1440>] " +
                "[--policy <reference>]. " + error);
            return;
        }

        var snapshot = await _promotion.InspectAsync(
            options.EvidenceId,
            _repoPath,
            cancellationToken);
        PrintReviewSnapshot(snapshot);
        if (!snapshot.Available)
            return;

        var actor = Environment.UserName;
        var policyReference = options.PolicyReference ??
            $"task-contract/{snapshot.TaskId}/approval";
        var validUntil = DateTime.UtcNow.AddMinutes(options.ValidMinutes);
        Console.WriteLine($"Authenticated actor: {actor}");
        Console.WriteLine($"Policy reference: {policyReference}");
        Console.WriteLine($"Decision validity: {validUntil:O}");
        Console.Write("Decision [approve/reject/abandon]: ");
        var decisionInput = Console.ReadLine()?.Trim().ToLowerInvariant();
        var decision = decisionInput switch
        {
            "approve" => CandidateReviewDecision.Approve,
            "reject" => CandidateReviewDecision.Reject,
            null or "" or "abandon" => CandidateReviewDecision.Abandon,
            _ => (CandidateReviewDecision?)null
        };
        if (!decision.HasValue)
        {
            Console.WriteLine("Decision must be approve, reject, or abandon.");
            return;
        }

        string justification;
        if (decision == CandidateReviewDecision.Abandon && string.IsNullOrEmpty(decisionInput))
        {
            justification = "Review input ended before an explicit decision";
        }
        else
        {
            Console.Write("Justification: ");
            justification = Console.ReadLine()?.Trim() ?? string.Empty;
        }

        var review = await _promotion.ReviewAsync(new CandidateReviewRequest
        {
            EvidenceId = snapshot.EvidenceId,
            RepositoryPath = _repoPath,
            ExpectedDiffHash = snapshot.DiffHash,
            Actor = actor,
            Decision = decision.Value,
            Justification = justification,
            ValidUntil = validUntil,
            PolicyReference = policyReference
        }, cancellationToken);
        PrintReviewResult(review);
        if (review.Status != CandidatePromotionStatus.Approved || !review.Persisted)
            return;

        var requiredConfirmation = $"PROMOTE {snapshot.DiffHash}";
        Console.WriteLine("The approval is persisted, but the repository is still unchanged.");
        Console.Write($"Type '{requiredConfirmation}' to promote now: ");
        var confirmation = Console.ReadLine()?.Trim();
        if (!string.Equals(confirmation, requiredConfirmation, StringComparison.Ordinal))
        {
            var abandonment = await _promotion.ReviewAsync(new CandidateReviewRequest
            {
                EvidenceId = snapshot.EvidenceId,
                RepositoryPath = _repoPath,
                ExpectedDiffHash = snapshot.DiffHash,
                Actor = actor,
                Decision = CandidateReviewDecision.Abandon,
                Justification = "Promotion confirmation was not supplied exactly as required",
                ValidUntil = validUntil,
                PolicyReference = policyReference,
                RelatedReviewId = review.Evidence.Id
            }, CancellationToken.None);
            PrintReviewResult(abandonment);
            Console.WriteLine("Repository unchanged; the approval can no longer authorize promotion.");
            return;
        }

        var promoted = await _promotion.PromoteAsync(new CandidatePromotionRequest
        {
            EvidenceId = snapshot.EvidenceId,
            RepositoryPath = _repoPath,
            ExpectedDiffHash = snapshot.DiffHash,
            Actor = actor,
            Approval = new PromotionApproval
            {
                Kind = PromotionApprovalKind.HumanReview,
                Reference = review.Evidence.ApprovalReference,
                ConfirmedAt = DateTime.UtcNow
            }
        }, cancellationToken);
        PrintPromotionResult(promoted);
    }

    private async Task ExportCandidatePatch(
        string argument,
        CancellationToken cancellationToken)
    {
        var tokens = argument.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);
        if (tokens.Length != 2 || !Guid.TryParse(tokens[0], out var evidenceId))
        {
            Console.WriteLine("Usage: export-patch <evidence-id> <output-path>");
            return;
        }

        var snapshot = await _promotion.InspectAsync(
            evidenceId,
            _repoPath,
            cancellationToken);
        PrintReviewSnapshot(snapshot);
        if (!snapshot.Available)
            return;
        var result = await _promotion.ExportPatchAsync(new CandidatePatchExportRequest
        {
            EvidenceId = evidenceId,
            DestinationPath = tokens[1],
            ExpectedDiffHash = snapshot.DiffHash,
            Actor = Environment.UserName
        }, cancellationToken);
        PrintPromotionResult(result);
    }

    private static void PrintReviewSnapshot(CandidateReviewSnapshot snapshot)
    {
        Console.WriteLine("AECS AUTHENTICATED CANDIDATE REVIEW");
        Console.WriteLine($"Evidence: {snapshot.EvidenceId:N}");
        if (!snapshot.Available)
        {
            Console.WriteLine($"Unavailable: {snapshot.Message}");
            return;
        }
        Console.WriteLine($"Candidate: {snapshot.CandidateId:N}");
        Console.WriteLine($"Baseline: {snapshot.BaselineCommit} ({snapshot.BaselineBranch})");
        Console.WriteLine($"Repository: {snapshot.RepositoryPath}");
        Console.WriteLine(
            $"Repository state: {(snapshot.RepositoryReady ? "ready" : "changed")} " +
            $"- {snapshot.RepositoryState}");
        Console.WriteLine($"Risk: {snapshot.Risk}");
        Console.WriteLine($"Decision: {snapshot.Decision}/{snapshot.State}");
        Console.WriteLine(
            $"Promotion eligibility: {snapshot.Eligibility}; reviewable={snapshot.Reviewable}");
        Console.WriteLine($"Diff hash: {snapshot.DiffHash}");
        Console.WriteLine("Changed files:");
        foreach (var file in snapshot.ChangedFiles)
            Console.WriteLine($"  {file}");
        Console.WriteLine("Gates:");
        foreach (var gate in snapshot.Gates)
        {
            Console.WriteLine(
                $"  [{gate.Phase}] {gate.Verifier}: {gate.Status} {gate.Message}".TrimEnd());
        }
        if (snapshot.Gates.Count == 0)
            Console.WriteLine("  (none recorded)");
        Console.WriteLine("Diff:");
        Console.WriteLine(snapshot.Diff);
    }

    private static void PrintReviewResult(CandidateReviewResult result)
    {
        Console.WriteLine($"Review status: {result.Status}");
        Console.WriteLine($"Review message: {result.Message}");
        Console.WriteLine($"Review evidence: {result.Evidence.Id:N}");
        Console.WriteLine($"Persisted: {result.Persisted}");
    }

    private static void PrintPromotionResult(CandidatePromotionResult result)
    {
        Console.WriteLine($"Promotion status: {result.Status}");
        Console.WriteLine($"Promotion message: {result.Message}");
        Console.WriteLine($"Promotion evidence: {result.Evidence.Id:N}");
        if (!string.IsNullOrWhiteSpace(result.OutputPath))
            Console.WriteLine($"Output: {result.OutputPath}");
    }

    private static bool TryParseReviewOptions(
        string argument,
        out JarvisReviewOptions options,
        out string error)
    {
        var tokens = argument.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        Guid? evidenceId = null;
        var validMinutes = 15;
        string? policyReference = null;
        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (token == "--evidence" && index + 1 < tokens.Length &&
                Guid.TryParse(tokens[++index], out var parsedEvidence))
            {
                evidenceId = parsedEvidence;
                continue;
            }
            if (token == "--valid-minutes" && index + 1 < tokens.Length &&
                int.TryParse(tokens[++index], out var parsedMinutes) &&
                parsedMinutes is >= 1 and <= 1440)
            {
                validMinutes = parsedMinutes;
                continue;
            }
            if (token == "--policy" && index + 1 < tokens.Length)
            {
                policyReference = tokens[++index];
                continue;
            }
            if (!token.StartsWith("--", StringComparison.Ordinal) &&
                evidenceId is null && Guid.TryParse(token, out var positionalEvidence))
            {
                evidenceId = positionalEvidence;
                continue;
            }
            options = new JarvisReviewOptions();
            error = $"Invalid argument '{token}'.";
            return false;
        }

        if (!evidenceId.HasValue || string.IsNullOrWhiteSpace(policyReference) &&
            tokens.Contains("--policy", StringComparer.Ordinal))
        {
            options = new JarvisReviewOptions();
            error = "A valid evidence ID and non-empty policy reference are required.";
            return false;
        }
        options = new JarvisReviewOptions
        {
            EvidenceId = evidenceId.Value,
            ValidMinutes = validMinutes,
            PolicyReference = policyReference
        };
        error = string.Empty;
        return true;
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

    private sealed class JarvisReviewOptions
    {
        public Guid EvidenceId { get; init; }
        public int ValidMinutes { get; init; } = 15;
        public string? PolicyReference { get; init; }
    }
}
