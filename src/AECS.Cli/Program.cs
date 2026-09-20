using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Application;
using AECS.Application.AdaptiveController;
using AECS.Application.Classification;
using AECS.Application.ControlKernel;
using AECS.Application.ContextCompiler;
using AECS.Application.Experiments;
using AECS.Application.EvidenceGraph;
using AECS.Application.Parsing;
using AECS.Application.Promotion;
using AECS.Application.Replay;
using AECS.Application.SemanticLinter;
using AECS.Application.Staging;
using AECS.Application.Verification;
using AECS.Cli;
using AECS.Cli.Jarvis;
using AECS.Cli.Runtime;
using AECS.Cli.Vscode;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.AgentRuntime;
using AECS.Infrastructure.Cryptography;
using AECS.Infrastructure.Processes;
using AECS.Infrastructure.Repositories;

// Load .env file if present
LoadEnvFile();

// Determine command
var command = args.Length > 0 ? args[0] : "jarvis";

if (command == "experiment")
    return await RunExperiment(args[1..]);
else if (command == "adaptive-experiment")
    return await RunAdaptiveExperiment(args[1..]);
else if (command == "jarvis")
    return await RunJarvis(args[1..]);
else if (command == "vscode-server")
    return await RunVscodeServer(args[1..]);
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
else if (command == "history")
    return await RunHistory(args[1..]);
else if (command == "adaptive-report")
    return await RunAdaptiveReport(args[1..]);
else if (command == "doctor")
    return await RunDoctor(args[1..]);
else if (command == "run")
    return await RunSingle(args[1..]);
else
    return await RunSingle(args);

static async Task<int> RunDoctor(string[] args)
{
    const string usage =
        "Usage: aecs doctor [--repo <path>] [--format <text|json>] " +
        RuntimeCliOptions.Usage;
    string? repositoryPath = null;
    var format = "text";
    var runtimeOptions = new RuntimeCliOptions();
    var invalidArgument = false;

    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--repo" && index + 1 < args.Length)
            repositoryPath = args[++index];
        else if (args[index] == "--format" && index + 1 < args.Length)
            format = args[++index];
        else if (runtimeOptions.TryConsume(args, ref index))
        {
        }
        else
            invalidArgument = true;
    }

    if (invalidArgument || format is not ("text" or "json"))
    {
        Console.WriteLine(usage);
        return 1;
    }

    var checks = new List<DoctorCheck>();
    ResolvedAecsRuntimeConfiguration? resolved = null;
    try
    {
        resolved = AecsRuntimeConfigurationResolver.Resolve(runtimeOptions);
        checks.Add(DoctorCheck.Ready(
            "runtime-config",
            "Runtime configuration resolved with secrets redacted."));
    }
    catch (Exception ex)
    {
        checks.Add(DoctorCheck.ConfigurationInvalid(
            "runtime-config",
            $"Runtime configuration failed closed: {ex.Message}"));
    }

    await AddDotnetCheckAsync(checks);
    await AddProcessCheckAsync(checks, "git", "git", ["--version"], required: true);

    if (!string.IsNullOrWhiteSpace(repositoryPath))
    {
        var fullPath = Path.GetFullPath(repositoryPath);
        checks.Add(Directory.Exists(fullPath)
            ? DoctorCheck.Ready("repository", $"Repository path exists: {fullPath}")
            : DoctorCheck.DependencyAbsent("repository", $"Repository path does not exist: {fullPath}"));
        if (Directory.Exists(fullPath))
            await AddProcessCheckAsync(
                checks,
                "repository-git",
                "git",
                ["-C", fullPath, "rev-parse", "--show-toplevel"],
                required: true);
    }

    if (resolved is not null)
    {
        var configuration = resolved.Effective;
        AddWritableDirectoryCheck(
            checks,
            "evidence-keys",
            configuration.EvidenceKeyDirectory.Value,
            required: true);

        if (configuration.EvidenceBackend.Value == "json")
        {
            AddWritableDirectoryCheck(
                checks,
                "evidence-json",
                configuration.EvidenceJsonRoot.Value,
                required: true);
        }
        else if (configuration.EvidenceBackend.Value == "postgres")
        {
            checks.Add(configuration.PostgreSqlConnection.Configured
                ? DoctorCheck.Ready("evidence-postgres", "PostgreSQL connection string is configured and redacted.")
                : DoctorCheck.ConfigurationInvalid("evidence-postgres", "PostgreSQL evidence store requires a configured connection string."));
        }

        await AddDockerCheckAsync(checks);
        await AddDockerImageCheckAsync(checks, SandboxExecutionProfile.DefaultImage);

        if (configuration.AgentMode.Value == "mock")
        {
            checks.Add(DoctorCheck.Ready("provider", "Mock provider selected; no model service required."));
        }
        else
        {
            await AddOllamaCheckAsync(checks, configuration.OllamaBaseUrl.Value);
        }

        checks.Add(configuration.CloudFallbackEnabled.Value
            ? configuration.CloudCredential.Configured && configuration.CloudRepositoryContextAllowed.Value
                ? DoctorCheck.Ready("cloud-fallback", "Cloud fallback is enabled with credential present and repository context allowed.")
                : DoctorCheck.ConfigurationInvalid("cloud-fallback", "Cloud fallback is enabled but credential or repository-context consent is missing.")
            : DoctorCheck.Ready("cloud-fallback", "Cloud fallback is disabled by policy."));
    }

    var summary = SummaryStatus(checks);

    if (format == "json")
    {
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            schemaVersion = "aecs.doctor/v1",
            status = summary,
            checks = checks.Select(check => new
            {
                check.Id,
                status = StatusCode(check.Status),
                required = check.Required,
                check.Message
            })
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase
        }));
    }
    else
    {
        Console.WriteLine($"AECS DOCTOR: {summary}");
        foreach (var check in checks)
        {
            var requirement = check.Required ? "required" : "optional";
            Console.WriteLine(
                $"  {check.Id.PadRight(18)} {StatusCode(check.Status),-24} {requirement}  {check.Message}");
        }
    }

    return summary == "ready" ? 0 : 1;
}

static async Task AddDotnetCheckAsync(List<DoctorCheck> checks)
{
    var result = await RunDiagnosticProcessAsync("dotnet", ["--version"]);
    if (!result.Started)
    {
        checks.Add(DoctorCheck.DependencyAbsent("dotnet", "dotnet executable was not found."));
        return;
    }

    var version = result.StandardOutput.Trim();
    if (!result.Succeeded || string.IsNullOrWhiteSpace(version))
    {
        checks.Add(DoctorCheck.ServiceUnavailable(
            "dotnet",
            $"dotnet --version failed: {ShortDiagnostic(result)}"));
        return;
    }

    checks.Add(version.StartsWith("10.0.", StringComparison.Ordinal) &&
        !version.Contains("preview", StringComparison.OrdinalIgnoreCase)
        ? DoctorCheck.Ready("dotnet", $"Stable .NET 10 SDK resolved: {version}.")
        : DoctorCheck.ConfigurationInvalid("dotnet", $"Expected stable .NET 10 SDK, resolved '{version}'."));
}

static async Task AddProcessCheckAsync(
    List<DoctorCheck> checks,
    string id,
    string fileName,
    IReadOnlyList<string> arguments,
    bool required)
{
    var result = await RunDiagnosticProcessAsync(fileName, arguments);
    if (!result.Started)
    {
        checks.Add(DoctorCheck.DependencyAbsent(id, $"{fileName} executable was not found.", required));
        return;
    }

    checks.Add(result.Succeeded
        ? DoctorCheck.Ready(id, FirstLine(result.StandardOutput), required)
        : DoctorCheck.ServiceUnavailable(id, ShortDiagnostic(result), required));
}

static async Task AddDockerCheckAsync(List<DoctorCheck> checks)
{
    await AddProcessCheckAsync(
        checks,
        "docker",
        "docker",
        ["version", "--format", "{{.Server.Version}}"],
        required: true);
}

static async Task AddDockerImageCheckAsync(
    List<DoctorCheck> checks,
    string image)
{
    var result = await RunDiagnosticProcessAsync(
        "docker",
        ["image", "inspect", image, "--format", "{{index .RepoDigests 0}}"]);
    if (!result.Started)
    {
        checks.Add(DoctorCheck.DependencyAbsent(
            "docker-image",
            "Docker executable was not found; staged image was not inspected.",
            required: true));
        return;
    }

    checks.Add(result.Succeeded
        ? DoctorCheck.Ready("docker-image", $"Pinned staged image is present: {FirstLine(result.StandardOutput)}")
        : DoctorCheck.ServiceUnavailable("docker-image", $"Pinned staged image is unavailable locally: {ShortDiagnostic(result)}"));
}

static async Task AddOllamaCheckAsync(List<DoctorCheck> checks, string baseUrl)
{
    using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    try
    {
        var endpoint = new Uri(new Uri(baseUrl.TrimEnd('/') + "/"), "api/tags");
        using var response = await client.GetAsync(endpoint);
        if (!response.IsSuccessStatusCode)
        {
            checks.Add(DoctorCheck.ServiceUnavailable(
                "provider",
                $"Ollama endpoint returned HTTP {(int)response.StatusCode}.",
                required: true));
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        var hasR0 = body.Contains("qwen2.5-coder:1.5b", StringComparison.OrdinalIgnoreCase);
        var hasR1Plus = body.Contains("qwen2.5-coder:3b", StringComparison.OrdinalIgnoreCase);
        if (hasR0 && hasR1Plus)
        {
            checks.Add(DoctorCheck.Ready("provider", "Ollama is reachable and required AECS models are listed."));
        }
        else
        {
            checks.Add(DoctorCheck.DependencyAbsent(
                "provider",
                "Ollama is reachable, but qwen2.5-coder:1.5b and/or qwen2.5-coder:3b were not listed.",
                required: true));
        }
    }
    catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException)
    {
        checks.Add(DoctorCheck.ServiceUnavailable(
            "provider",
            $"Ollama endpoint is unavailable: {ex.Message}",
            required: true));
    }
}

static void AddWritableDirectoryCheck(
    List<DoctorCheck> checks,
    string id,
    string path,
    bool required)
{
    try
    {
        var fullPath = Path.GetFullPath(path);
        var probeDirectory = Directory.Exists(fullPath)
            ? fullPath
            : Directory.GetParent(fullPath)?.FullName;
        if (probeDirectory is null || !Directory.Exists(probeDirectory))
        {
            checks.Add(DoctorCheck.ConfigurationInvalid(
                id,
                $"Directory or parent does not exist: {fullPath}",
                required));
            return;
        }

        var probe = Path.Combine(probeDirectory, $".aecs-doctor-{Guid.NewGuid():N}.tmp");
        File.WriteAllText(probe, "probe");
        File.Delete(probe);
        checks.Add(DoctorCheck.Ready(id, $"Writable path available: {fullPath}", required));
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
    {
        checks.Add(DoctorCheck.ConfigurationInvalid(
            id,
            $"Path is not writable: {ex.Message}",
            required));
    }
}

static async Task<DiagnosticProcessResult> RunDiagnosticProcessAsync(
    string fileName,
    IReadOnlyList<string> arguments)
{
    try
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = fileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        foreach (var argument in arguments)
            process.StartInfo.ArgumentList.Add(argument);
        if (!process.Start())
            return DiagnosticProcessResult.NotStarted();

        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!await Task.Run(() => process.WaitForExit(3000)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            return new DiagnosticProcessResult(true, false, -1, await stdout, await stderr, TimedOut: true);
        }

        return new DiagnosticProcessResult(
            true,
            process.ExitCode == 0,
            process.ExitCode,
            await stdout,
            await stderr,
            TimedOut: false);
    }
    catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
    {
        return DiagnosticProcessResult.NotStarted();
    }
}

static string StatusCode(DoctorStatus status) => status switch
{
    DoctorStatus.Ready => "ready",
    DoctorStatus.DependencyAbsent => "dependency_absent",
    DoctorStatus.ConfigurationInvalid => "configuration_invalid",
    DoctorStatus.ServiceUnavailable => "service_unavailable",
    _ => "configuration_invalid"
};

static string SummaryStatus(IEnumerable<DoctorCheck> checks)
{
    var failing = checks
        .Where(check => check.Required && check.Status != DoctorStatus.Ready)
        .Select(check => check.Status)
        .ToHashSet();
    if (failing.Contains(DoctorStatus.ConfigurationInvalid))
        return "configuration_invalid";
    if (failing.Contains(DoctorStatus.DependencyAbsent))
        return "dependency_absent";
    if (failing.Contains(DoctorStatus.ServiceUnavailable))
        return "service_unavailable";
    return "ready";
}

static string FirstLine(string value)
{
    var line = value.Split(
            ['\r', '\n'],
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .FirstOrDefault();
    return string.IsNullOrWhiteSpace(line) ? "command completed" : line;
}

static string ShortDiagnostic(DiagnosticProcessResult result)
{
    if (result.TimedOut)
        return "command timed out";
    var text = FirstLine(result.StandardError);
    if (text == "command completed")
        text = FirstLine(result.StandardOutput);
    return $"{text} (exit {result.ExitCode})";
}

static async Task<int> RunAdaptiveReport(string[] args)
{
    const string usage =
        "Usage: aecs adaptive-report --repo <path> [--limit <1-500>] " +
        "[--format <text|json>] " + EvidenceStoreSelection.Usage;
    string? repositoryPath = null;
    var limit = 500;
    var format = "text";
    var selection = new EvidenceStoreSelection();
    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--repo" && index + 1 < args.Length)
            repositoryPath = args[++index];
        else if (args[index] == "--limit" && index + 1 < args.Length &&
                 int.TryParse(args[++index], out var parsedLimit))
            limit = parsedLimit;
        else if (args[index] == "--format" && index + 1 < args.Length)
            format = args[++index];
        else if (selection.TryConsume(args, ref index))
        {
        }
        else
        {
            Console.WriteLine(usage);
            return 1;
        }
    }
    if (repositoryPath is null || limit is < 1 or > 500 ||
        format is not ("text" or "json"))
    {
        Console.WriteLine(usage);
        return 1;
    }
    if (!TryCreateEvidenceStore(selection, out var store))
        return 1;
    if (store is not IEvidenceGraphSource graphSource)
    {
        Console.WriteLine("ERROR: selected evidence store does not support authenticated history queries.");
        return 1;
    }
    try
    {
        var report = await new AdaptiveController(store, graphSource).CreateReportAsync(
            repositoryPath,
            limit,
            CancellationToken.None);
        Console.Write(format == "json"
            ? AdaptiveShadowReportFormatter.ToJson(report) + Environment.NewLine
            : AdaptiveShadowReportFormatter.ToText(report));
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: adaptive shadow report failed closed: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunHistory(string[] args)
{
    const string usage =
        "Usage: aecs history <ingest|review|suppress|list> " +
        "[--file <decision.json>] [--id <id>] [--version <n>] " +
        "[--actor <actor>] [--reason <reason>] [--approve|--reject] " +
        "[--blocking|--advisory] [--decision <id>] [--decision-version <n>] " +
        "[--expires <ISO-8601>] [--symbol <id>] [--path <repo-path>] " +
        "[--format <text|json>] " + EvidenceStoreSelection.Usage;
    if (args.Length == 0 || args[0] is not ("ingest" or "review" or "suppress" or "list"))
    {
        Console.WriteLine(usage);
        return 1;
    }

    var operation = args[0];
    string? file = null;
    string? id = null;
    string? actor = null;
    string? reason = null;
    string? decisionId = null;
    string? expires = null;
    string? symbolId = null;
    string? filePath = null;
    var version = 0;
    var decisionVersion = 0;
    bool? approved = null;
    var enforcement = HistoricalDecisionEnforcement.Advisory;
    var format = "text";
    var evidenceStoreSelection = new EvidenceStoreSelection();
    for (var index = 1; index < args.Length; index++)
    {
        if (args[index] == "--file" && index + 1 < args.Length)
            file = args[++index];
        else if (args[index] == "--id" && index + 1 < args.Length)
            id = args[++index];
        else if (args[index] == "--version" && index + 1 < args.Length &&
                 int.TryParse(args[++index], out var parsedVersion))
            version = parsedVersion;
        else if (args[index] == "--actor" && index + 1 < args.Length)
            actor = args[++index];
        else if (args[index] == "--reason" && index + 1 < args.Length)
            reason = args[++index];
        else if (args[index] == "--decision" && index + 1 < args.Length)
            decisionId = args[++index];
        else if (args[index] == "--decision-version" && index + 1 < args.Length &&
                 int.TryParse(args[++index], out var parsedDecisionVersion))
            decisionVersion = parsedDecisionVersion;
        else if (args[index] == "--expires" && index + 1 < args.Length)
            expires = args[++index];
        else if (args[index] == "--symbol" && index + 1 < args.Length)
            symbolId = args[++index];
        else if (args[index] == "--path" && index + 1 < args.Length)
            filePath = args[++index].Replace('\\', '/');
        else if (args[index] == "--approve")
            approved = true;
        else if (args[index] == "--reject")
            approved = false;
        else if (args[index] == "--blocking")
            enforcement = HistoricalDecisionEnforcement.Blocking;
        else if (args[index] == "--advisory")
            enforcement = HistoricalDecisionEnforcement.Advisory;
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

    if (!TryCreateEvidenceStore(evidenceStoreSelection, out var executionStore))
        return 1;
    if (executionStore is not IHistoricalDecisionStore historyStore)
    {
        Console.WriteLine("ERROR: selected operational store has no historical registry.");
        return 1;
    }

    var registry = new HistoricalDecisionRegistry(historyStore);
    var jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = true
    };
    jsonOptions.Converters.Add(new JsonStringEnumConverter());
    try
    {
        if (operation == "ingest")
        {
            if (file is null || !File.Exists(file))
            {
                Console.WriteLine(usage);
                return 1;
            }
            var decision = JsonSerializer.Deserialize<HistoricalDecision>(
                    await File.ReadAllTextAsync(file),
                    jsonOptions) ??
                throw new InvalidOperationException("Historical decision input is empty.");
            var location = await registry.IngestAsync(
                decision,
                DateTime.UtcNow,
                CancellationToken.None);
            Console.WriteLine($"Draft historical decision persisted: {location}");
            return 0;
        }

        if (operation == "review")
        {
            if (id is null || version <= 0 || actor is null || reason is null ||
                approved is null)
            {
                Console.WriteLine(usage);
                return 1;
            }
            var location = await registry.ReviewAsync(
                id,
                version,
                actor,
                reason,
                approved.Value,
                enforcement,
                DateTime.UtcNow,
                CancellationToken.None);
            Console.WriteLine($"Human review revision persisted: {location}");
            return 0;
        }

        if (operation == "suppress")
        {
            if (id is null || version <= 0 || decisionId is null || decisionVersion <= 0 ||
                actor is null || reason is null ||
                !DateTimeOffset.TryParse(expires, out var parsedExpiration))
            {
                Console.WriteLine(usage);
                return 1;
            }
            var location = await registry.SuppressAsync(new HistoricalDecisionSuppression
            {
                Id = id,
                Version = version,
                DecisionId = decisionId,
                DecisionVersion = decisionVersion,
                Actor = actor,
                Reason = reason,
                SymbolId = symbolId ?? string.Empty,
                FilePath = filePath ?? string.Empty,
                CreatedAt = DateTime.UtcNow,
                ExpiresAt = parsedExpiration.UtcDateTime
            }, CancellationToken.None);
            Console.WriteLine($"Versioned suppression persisted: {location}");
            return 0;
        }

        if (format is not ("text" or "json"))
        {
            Console.WriteLine(usage);
            return 1;
        }
        var decisions = await registry.ListAsync(CancellationToken.None);
        if (format == "json")
        {
            Console.WriteLine(JsonSerializer.Serialize(decisions, jsonOptions));
        }
        else if (decisions.Count == 0)
        {
            Console.WriteLine("No historical decisions are registered.");
        }
        else
        {
            foreach (var decision in decisions)
            {
                Console.WriteLine(
                    $"{decision.Id} v{decision.Version} {decision.Type} " +
                    $"{decision.Review.Status}/{decision.Enforcement} " +
                    $"{decision.Source}@{decision.SourceVersion}");
            }
        }
        return 0;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: historical registry operation failed closed: {ex.Message}");
        return 1;
    }
}

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
    var runtimeOptions = new RuntimeCliOptions();

    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--repo" && index + 1 < args.Length)
            repositoryPath = args[++index];
        else if (args[index] == "--evidence" && index + 1 < args.Length)
            evidenceIdValue = args[++index];
        else if (runtimeOptions.TryConsume(args, ref index))
        {
        }
        else
        {
            Console.WriteLine(
                "Usage: aecs replay --repo <path> --evidence <id> " +
                RuntimeCliOptions.Usage);
            return 1;
        }
    }

    if (repositoryPath is null || !Guid.TryParse(evidenceIdValue, out var evidenceId))
    {
        Console.WriteLine(
            "Usage: aecs replay --repo <path> --evidence <id> " +
            RuntimeCliOptions.Usage);
        return 1;
    }

    if (!TryCreateRuntime(runtimeOptions, out var createdRuntime))
        return 1;
    using var runtime = createdRuntime;
    PrintEffectiveRuntime(runtime.Configuration, runtimeOptions.ShowEffectiveConfiguration);

    try
    {
        var processRunner = new SystemProcessRunner();
        var result = await new ExecutionReplayService(
            processRunner,
            runtime.EvidenceStore,
            runtime.CreateStagedProcessRunnerFactory(processRunner))
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
        if (result.Evidence.ExpectedRepositorySnapshotHash is not null)
        {
            Console.WriteLine(
                $"Expected repository snapshot: {result.Evidence.ExpectedRepositorySnapshotHash}");
            Console.WriteLine(
                $"Actual repository snapshot: {result.Evidence.ActualRepositorySnapshotHash ?? "missing"}");
            if (result.Evidence.RepositorySnapshotDiff is not null)
            {
                Console.WriteLine(
                    $"Repository snapshot diff: +{result.Evidence.RepositorySnapshotDiff.AddedFiles.Count} " +
                    $"-{result.Evidence.RepositorySnapshotDiff.RemovedFiles.Count} " +
                    $"~{result.Evidence.RepositorySnapshotDiff.ChangedFiles.Count}");
            }
        }
        if (result.Evidence.ExpectedCSharpSymbolGraphHash is not null)
        {
            Console.WriteLine(
                $"Expected C# symbol graph: {result.Evidence.ExpectedCSharpSymbolGraphHash}");
            Console.WriteLine(
                $"Actual C# symbol graph: {result.Evidence.ActualCSharpSymbolGraphHash ?? "missing"}");
        }
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

static bool TryCreateRuntime(
    RuntimeCliOptions options,
    out AecsExecutionRuntime runtime,
    TextWriter? errorWriter = null)
{
    try
    {
        var configuration = AecsRuntimeConfigurationResolver.Resolve(options);
        runtime = AecsExecutionRuntime.Create(configuration);
        return true;
    }
    catch (Exception ex)
    {
        runtime = null!;
        (errorWriter ?? Console.Out).WriteLine(
            $"ERROR: runtime configuration failed closed: {ex.Message}");
        return false;
    }
}

static void PrintEffectiveRuntime(
    EffectiveAecsRuntimeConfiguration configuration,
    bool json)
{
    Console.Write(json
        ? AecsRuntimeConfigurationResolver.ToJson(configuration) + Environment.NewLine
        : AecsRuntimeConfigurationResolver.ToText(configuration));
}

static int RunEvidenceKey(string[] args)
{
    const string usage =
        "Usage: aecs evidence-key rotate [--runtime-config <config.json>] " +
        "[--key-directory <path>] [--show-effective-config]";
    if (args.Length == 0 || !string.Equals(args[0], "rotate", StringComparison.Ordinal))
    {
        Console.WriteLine(usage);
        return 1;
    }

    var runtimeOptions = new RuntimeCliOptions();
    for (var index = 1; index < args.Length; index++)
    {
        if (args[index] is "--runtime-config" or "--key-directory" or
            "--show-effective-config" && runtimeOptions.TryConsume(args, ref index))
        {
        }
        else
        {
            Console.WriteLine(usage);
            return 1;
        }
    }

    try
    {
        var resolved = AecsRuntimeConfigurationResolver.Resolve(runtimeOptions);
        PrintEffectiveRuntime(
            resolved.Effective,
            runtimeOptions.ShowEffectiveConfiguration);
        var keyDirectory = resolved.Effective.EvidenceKeyDirectory.Value;
        var newKeyId = RsaEvidenceSignatureService.RotateKey(keyDirectory);
        Console.WriteLine($"Evidence signing key rotated: {newKeyId}");
        Console.WriteLine($"Trusted public keys retained in: {Path.GetFullPath(keyDirectory)}");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
        CryptographicException or InvalidOperationException)
    {
        Console.WriteLine($"ERROR: evidence key rotation failed: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunExperiment(string[] args)
{
    string? repoPath = null;
    string? tasksDir = null;
    string? datasetPath = null;
    string? outputDirectory = null;
    string? costReconciliationPath = null;
    bool resume = false;
    bool includeRealProviders = false;
    var runtimeOptions = new RuntimeCliOptions();
    var invalidArgument = false;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--tasks" && i + 1 < args.Length)
            tasksDir = args[++i];
        else if (args[i] == "--dataset" && i + 1 < args.Length)
            datasetPath = args[++i];
        else if (args[i] == "--output" && i + 1 < args.Length)
            outputDirectory = args[++i];
        else if (args[i] == "--cost-reconciliation" && i + 1 < args.Length)
            costReconciliationPath = args[++i];
        else if (args[i] == "--resume")
            resume = true;
        else if (args[i] == "--include-real-providers")
            includeRealProviders = true;
        else if (runtimeOptions.TryConsume(args, ref i))
        {
        }
        else
            invalidArgument = true;
    }

    const string datasetUsage =
        "aecs experiment --dataset <manifest.json> --output <directory> " +
        "[--resume] [--include-real-providers] " +
        "[--cost-reconciliation <ledger.json>] ";
    const string legacyUsage =
        "aecs experiment --repo <path> --tasks <dir> ";
    if (invalidArgument || datasetPath is not null &&
            (repoPath is not null || tasksDir is not null ||
             runtimeOptions.AgentMode == "mock" || runtimeOptions.CloudModel is not null) ||
        datasetPath is null && (repoPath is null || tasksDir is null ||
            costReconciliationPath is not null) ||
        datasetPath is not null && outputDirectory is null)
    {
        Console.WriteLine(
            $"Usage: {datasetUsage}{RuntimeCliOptions.Usage}\n" +
            $"       {legacyUsage}{RuntimeCliOptions.Usage}");
        return 1;
    }

    if (!TryCreateRuntime(runtimeOptions, out var createdRuntime))
        return 1;
    using var runtime = createdRuntime;
    PrintEffectiveRuntime(runtime.Configuration, runtimeOptions.ShowEffectiveConfiguration);

    if (datasetPath is not null)
    {
        try
        {
            var dataset = ExperimentDatasetLoader.Load(datasetPath);
            var processRunner = new SystemProcessRunner();
            var baseline = await new GitWorkspaceManager(processRunner).CaptureBaselineAsync(
                dataset.RepositoryPath,
                CancellationToken.None);
            var artifacts = new ExperimentArtifactStore(outputDirectory!);
            var costReconciliation = costReconciliationPath is null
                ? null
                : ExperimentCostReconciliationLoader.Load(costReconciliationPath);
            var parser = new TaskContractParser();
            using var experimentHttpClient = new HttpClient();
            var runner = new ExperimentRunner(async (definition, cancellationToken) =>
            {
                var runBaseline = await new GitWorkspaceManager(processRunner)
                    .CaptureBaselineAsync(definition.RepositoryPath, cancellationToken);
                if (!runBaseline.Commit.Equals(
                        definition.BaselineCommit,
                        StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Dataset repository baseline changed between repetitions.");
                }
                var agent = BuildExperimentAgent(
                    definition,
                    runtime,
                    experimentHttpClient);
                var pipeline = runtime.CreatePipeline(
                    new RepositoryContextCompiler(
                        defaultOptions: definition.Variant.Context,
                        selectionStrategy: definition.Variant.ContextStrategy),
                    new FixedModelExecutionController(definition.Variant.Model),
                    agent,
                    definition.Variant.ContextStrategy == ContextStrategyIds.GraphRanked &&
                    definition.Task.RequiredContextPaths.Count > 0
                        ? new RequiredContextPathsGate(definition.Task.RequiredContextPaths)
                        : null,
                    constraintLedgerEnabled: definition.Variant.ConstraintLedgerEnabled);
                return await pipeline.RunAsync(
                    definition.RepositoryPath,
                    parser.ParseFromFile(definition.ContractPath),
                    cancellationToken);
            });
            var datasetReport = await runner.RunDatasetAsync(
                dataset,
                baseline.Commit,
                artifacts,
                resume,
                includeRealProviders,
                costReconciliation,
                CancellationToken.None);
            Console.WriteLine(ExperimentReportFormatter.Format(datasetReport));
            Console.WriteLine($"JSON report: {artifacts.ReportPath}");
            Console.WriteLine($"CSV results: {artifacts.ResultsCsvPath}");
            Console.WriteLine($"CSV comparisons: {artifacts.ComparisonsCsvPath}");
            Console.WriteLine($"CSV analysis: {artifacts.AnalysisCsvPath}");
            Console.WriteLine($"CSV cost records: {artifacts.CostRecordsCsvPath}");
            Console.WriteLine($"CSV cost efficiency: {artifacts.CostEfficiencyCsvPath}");
            return datasetReport.Succeeded ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"ERROR: experiment failed closed: {ex.Message}");
            return 1;
        }
    }

    var taskFiles = Directory.GetFiles(tasksDir!, "*.yaml")
        .Concat(Directory.GetFiles(tasksDir!, "*.yml"))
        .OrderBy(f => f)
        .ToList();

    if (taskFiles.Count == 0)
    {
        Console.WriteLine($"No YAML task files found in {tasksDir}");
        return 1;
    }

    ExperimentReport report;
    try
    {
        var runner = new ExperimentRunner(runtime.CreatePipeline());
        report = await runner.RunAsync(repoPath!, taskFiles, CancellationToken.None);
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: experiment failed closed: {ex.Message}");
        return 1;
    }

    Console.WriteLine(ExperimentReportFormatter.Format(report));

    return 0;
}

static async Task<int> RunAdaptiveExperiment(string[] args)
{
    const string usage =
        "Usage: aecs adaptive-experiment --dataset <manifest.json> --output <directory> " +
        "--include-real-providers [--resume] " + RuntimeCliOptions.Usage;
    string? datasetPath = null;
    string? outputDirectory = null;
    var resume = false;
    var includeRealProviders = false;
    var invalidArgument = false;
    var runtimeOptions = new RuntimeCliOptions();

    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--dataset" && index + 1 < args.Length)
            datasetPath = args[++index];
        else if (args[index] == "--output" && index + 1 < args.Length)
            outputDirectory = args[++index];
        else if (args[index] == "--resume")
            resume = true;
        else if (args[index] == "--include-real-providers")
            includeRealProviders = true;
        else if (runtimeOptions.TryConsume(args, ref index))
        {
        }
        else
            invalidArgument = true;
    }

    if (invalidArgument || datasetPath is null || outputDirectory is null ||
        !includeRealProviders || runtimeOptions.AgentMode == "mock")
    {
        Console.WriteLine(usage);
        return 1;
    }

    if (!TryCreateRuntime(runtimeOptions, out var createdRuntime))
        return 1;
    using var runtime = createdRuntime;
    PrintEffectiveRuntime(runtime.Configuration, runtimeOptions.ShowEffectiveConfiguration);
    if (runtime.EvidenceStore is not IEvidenceGraphSource graphSource)
    {
        Console.WriteLine(
            "ERROR: adaptive offline experiment requires an authenticated evidence graph source.");
        return 1;
    }

    try
    {
        var dataset = AdaptiveOfflineDatasetLoader.Load(datasetPath);
        var processRunner = new SystemProcessRunner();
        var artifacts = new AdaptiveOfflineArtifactStore(outputDirectory);
        using var experimentHttpClient = new HttpClient();
        var preflight = new AdaptiveOfflinePreflight(runtime.EvidenceStore, graphSource);
        var executor = new DelegatingAdaptiveOfflineArmExecutor(
            async (request, cancellationToken) =>
            {
                var provider = request.Provider.Kind == AdaptiveOfflineProvider.Local
                    ? ExperimentProvider.Local
                    : ExperimentProvider.Cloud;
                var variant = new ExperimentVariantDefinition
                {
                    Id = request.Arm == AdaptiveOfflineArm.Fixed ? "fixed" : "recommended",
                    Provider = provider,
                    Model = request.Plan.Model,
                    ContextStrategy = request.ContextStrategy,
                    Context = ContextCompilationOptions.FromBudget(request.Plan.Budget),
                    RequiresRealProvider = true,
                    Seed = request.EffectiveSeed,
                    Parameters = new Dictionary<string, string>(
                        request.Provider.Parameters,
                        StringComparer.Ordinal)
                };
                var definition = new ExperimentRunDefinition
                {
                    DatasetId = request.DatasetId,
                    DatasetHash = request.DatasetHash,
                    RunKey = request.PairKey,
                    RepositoryPath = request.RepositoryPath,
                    BaselineCommit = request.BaselineCommit,
                    Task = new ExperimentTaskDefinition
                    {
                        Id = request.Task.Id,
                        ExpectedDecision = request.Task.ExpectedDecision
                    },
                    Variant = variant,
                    Repetition = request.Repetition,
                    EffectiveSeed = request.EffectiveSeed
                };
                var agent = BuildExperimentAgent(
                    definition,
                    runtime,
                    experimentHttpClient);
                var pipeline = runtime.CreatePipeline(
                    new RepositoryContextCompiler(
                        defaultOptions: variant.Context,
                        selectionStrategy: request.ContextStrategy),
                    new AdaptiveOfflineExecutionController(request.Plan),
                    agent);
                return await pipeline.RunAsync(
                    request.RepositoryPath,
                    request.Contract,
                    cancellationToken);
            });
        var runner = new AdaptiveOfflineRunner(
            preflight,
            executor,
            new AdaptiveOfflineTaskBaselineVerifier(processRunner));
        AdaptiveOfflineReport report;
        if (dataset.IsMultiBaseline)
        {
            report = await runner.RunAsync(
                dataset,
                artifacts,
                resume,
                CancellationToken.None);
        }
        else
        {
            var baseline = await new GitWorkspaceManager(processRunner).CaptureBaselineAsync(
                dataset.RepositoryPath,
                CancellationToken.None);
            report = await runner.RunAsync(
                dataset,
                baseline.Commit,
                artifacts,
                resume,
                CancellationToken.None);
        }
        Console.WriteLine("AECS ADAPTIVE OFFLINE REPORT");
        Console.WriteLine($"Dataset: {report.DatasetId} v{report.DatasetVersion}");
        Console.WriteLine($"Pairs: {report.Analysis.CompletedPairs}/{report.Analysis.PlannedPairs}");
        Console.WriteLine($"Distinct tasks: {report.Analysis.DistinctCompletedTasks}");
        Console.WriteLine($"Conclusion: {report.Analysis.Conclusion}");
        Console.WriteLine($"Reason: {report.Analysis.ConclusionReason}");
        Console.WriteLine($"JSON report: {artifacts.ReportPath}");
        return report.Analysis.Conclusion == HypothesisConclusion.Maintain ? 0 : 1;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"ERROR: adaptive offline experiment failed closed: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunSingle(string[] args)
{
    string? repoPath = null;
    string? taskFile = null;
    var runtimeOptions = new RuntimeCliOptions();
    var invalidArgument = false;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--task-file" && i + 1 < args.Length)
            taskFile = args[++i];
        else if (runtimeOptions.TryConsume(args, ref i))
        {
        }
        else
            invalidArgument = true;
    }

    if (invalidArgument || repoPath is null || taskFile is null)
    {
        Console.WriteLine(
            "Usage: aecs run --repo <path> --task-file <path> " +
            RuntimeCliOptions.Usage);
        Console.WriteLine(
            "       aecs experiment --repo <path> --tasks <dir> " +
            RuntimeCliOptions.Usage);
        Console.WriteLine(
            "       aecs experiment --dataset <manifest.json> --output <directory> " +
            "[--resume] [--include-real-providers] " +
            "[--cost-reconciliation <ledger.json>] " + RuntimeCliOptions.Usage);
        Console.WriteLine(
            "       aecs adaptive-experiment --dataset <manifest.json> " +
            "--output <directory> --include-real-providers [--resume] " +
            RuntimeCliOptions.Usage);
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
        Console.WriteLine(
            "       aecs adaptive-report --repo <path> [--limit <1-500>] " +
            "[--format <text|json>] " + EvidenceStoreSelection.Usage);
        Console.WriteLine(
            "       aecs doctor [--repo <path>] [--format <text|json>] " +
            RuntimeCliOptions.Usage);
        Console.WriteLine(
            "       aecs evidence-key rotate [--runtime-config <config.json>] " +
            "[--key-directory <path>]");
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

    if (!TryCreateRuntime(runtimeOptions, out var createdRuntime))
        return 1;
    using var runtime = createdRuntime;
    PrintEffectiveRuntime(runtime.Configuration, runtimeOptions.ShowEffectiveConfiguration);

    StagedExecutionResult execution;
    try
    {
        execution = await runtime.CreatePipeline().RunAsync(
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
    {
        Console.WriteLine($"  {result.Verifier.PadRight(24)} {result.Status}");
        if (result.TestSuite is not null)
        {
            Console.WriteLine(
                $"    {result.TestSuite.Mode}: discovered={result.TestSuite.Discovered}, " +
                $"executed={result.TestSuite.Executed}, passed={result.TestSuite.Passed}, " +
                $"failed={result.TestSuite.Failed}, skipped={result.TestSuite.Skipped} " +
                $"[{result.TestSuite.Target}]");
        }
        PrintSemanticEvidence(result);
    }
    if (execution.BaselineVerificationResults.Count == 0)
        Console.WriteLine("  (not required)");
    Console.WriteLine($"Repository snapshot: {execution.RepositorySnapshot.SnapshotHash}");
    Console.WriteLine(
        $"Repository inventory: {execution.RepositorySnapshot.Files.Count} files, " +
        $"{execution.RepositorySnapshot.Projects.Count} projects, " +
        $"{execution.RepositorySnapshot.Solutions.Count} solutions, " +
        $"{execution.RepositorySnapshot.TestSuites.Count} test suites");
    Console.WriteLine($"C# symbol graph: {execution.CSharpSymbolGraph.GraphHash}");
    Console.WriteLine(
        $"Semantic inventory: {execution.CSharpSymbolGraph.Projects.Count} projects, " +
        $"{execution.CSharpSymbolGraph.Nodes.Count} nodes, " +
        $"{execution.CSharpSymbolGraph.Edges.Count} edges, " +
        $"{execution.CSharpSymbolGraph.Diagnostics.Count} diagnostics " +
        $"(loaded={execution.CSharpSymbolGraph.LoadSucceeded})");
    Console.WriteLine($"Context: {execution.ContextManifest.Id}");
    Console.WriteLine($"Context files: {execution.ContextManifest.Files.Count} " +
        $"included, {execution.ContextManifest.OmittedFileCount} omitted");
    Console.WriteLine($"Context tokens: {execution.ContextManifest.EstimatedTokens}/" +
        $"{execution.ContextManifest.MaxTokens} via " +
        $"{execution.ContextManifest.Tokenizer}@{execution.ContextManifest.TokenizerVersion}");
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
    {
        Console.WriteLine($"  {result.Verifier.PadRight(24)} {result.Status}");
        if (result.TestSuite is not null)
        {
            Console.WriteLine(
                $"    {result.TestSuite.Mode}: discovered={result.TestSuite.Discovered}, " +
                $"executed={result.TestSuite.Executed}, passed={result.TestSuite.Passed}, " +
                $"failed={result.TestSuite.Failed}, skipped={result.TestSuite.Skipped} " +
                $"[{result.TestSuite.Target}]");
        }
        PrintSemanticEvidence(result);
    }

    var securityResults = execution.BaselineVerificationResults
        .Concat(execution.VerificationResults)
        .Where(result => result.SecurityScan is not null)
        .ToList();
    if (securityResults.Count > 0)
    {
        Console.WriteLine("Security findings:");
        foreach (var result in securityResults)
        {
            var phase = result.SecurityScan!.IsBaseline ? "baseline" : "candidate";
            foreach (var finding in result.SecurityScan.Findings)
            {
                Console.WriteLine(
                    $"  {phase,-9} {finding.Severity,-8} {finding.Disposition,-10} " +
                    $"{finding.Rule} {finding.Path}:{finding.Line}");
            }
        }
        if (securityResults.All(result => result.SecurityScan!.Findings.Count == 0))
            Console.WriteLine("  (none)");
    }

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

static void PrintSemanticEvidence(VerificationResult result)
{
    if (result.Semantic is not null)
    {
        Console.WriteLine(
            $"    semantic baseline={result.Semantic.BaselineGraphHash} " +
            $"candidate={result.Semantic.CandidateGraphHash} " +
            $"impacted={result.Semantic.ImpactedFiles.Count}");
        foreach (var finding in result.Semantic.Findings.Take(5))
        {
            Console.WriteLine(
                $"    {finding.RuleId} [{finding.Severity}] {finding.Symbol} " +
                $"@ {finding.FilePath}: {finding.Justification}");
        }
        if (result.Semantic.Findings.Count > 5)
        {
            Console.WriteLine(
                $"    ({result.Semantic.Findings.Count - 5} additional semantic findings)");
        }
    }

    if (result.Historical is not null)
    {
        Console.WriteLine(
            $"    history={result.Historical.Status} " +
            $"decisions={result.Historical.Decisions.Count} " +
            $"conflicts={result.Historical.Conflicts.Count}");
        foreach (var conflict in result.Historical.Conflicts.Take(5))
        {
            var suppression = conflict.Suppressed
                ? $" suppressed={conflict.SuppressionId}/v{conflict.SuppressionVersion}"
                : string.Empty;
            Console.WriteLine(
                $"    {conflict.RuleId} [{conflict.Severity}] {conflict.Symbol} " +
                $"@ {conflict.FilePath}{suppression}: {conflict.Justification}");
        }
    }
}

static async Task<int> RunVscodeServer(string[] args)
{
    string? repositoryPath = null;
    string? stateDirectory = null;
    var runtimeOptions = new RuntimeCliOptions();
    var invalidArgument = false;
    for (var index = 0; index < args.Length; index++)
    {
        if (args[index] == "--repo" && index + 1 < args.Length)
            repositoryPath = args[++index];
        else if (args[index] == "--state-dir" && index + 1 < args.Length)
            stateDirectory = args[++index];
        else if (runtimeOptions.TryConsume(args, ref index))
        {
        }
        else
            invalidArgument = true;
    }

    var token = Environment.GetEnvironmentVariable(
        VscodeProtocolConstants.TokenEnvironmentVariable);
    if (invalidArgument || runtimeOptions.ShowEffectiveConfiguration ||
        string.IsNullOrWhiteSpace(repositoryPath) ||
        string.IsNullOrWhiteSpace(stateDirectory) ||
        string.IsNullOrWhiteSpace(token) || token.Length < 32)
    {
        Console.Error.WriteLine(
            "Usage: aecs vscode-server --repo <path> --state-dir <path> " +
            RuntimeCliOptions.Usage.Replace(" [--show-effective-config]", string.Empty));
        Console.Error.WriteLine(
            $"{VscodeProtocolConstants.TokenEnvironmentVariable} must contain a session token of at least 32 characters.");
        return 1;
    }

    try
    {
        repositoryPath = Path.GetFullPath(repositoryPath);
        stateDirectory = Path.GetFullPath(stateDirectory);
        if (!Directory.Exists(repositoryPath))
            throw new DirectoryNotFoundException("The repository directory does not exist.");
        Directory.CreateDirectory(stateDirectory);

        if (!TryCreateRuntime(runtimeOptions, out var runtime, Console.Error))
            return 1;
        using (runtime)
        {
            var operations = new VscodeOperationStore(stateDirectory);
            await using var backend = new VscodeExecutionBackend(
                repositoryPath,
                runtime,
                operations);
            await backend.RecoverAsync(CancellationToken.None);
            var session = new VscodeProtocolSession(token, backend);
            using var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelHandler = (_, eventArgs) =>
            {
                eventArgs.Cancel = true;
                shutdown.Cancel();
            };
            Console.CancelKeyPress += cancelHandler;
            try
            {
                await session.RunAsync(Console.In, Console.Out, shutdown.Token);
            }
            finally
            {
                Console.CancelKeyPress -= cancelHandler;
            }
        }
        return 0;
    }
    catch (OperationCanceledException)
    {
        return 0;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"ERROR: VS Code backend failed closed: {ex.Message}");
        return 1;
    }
}

static async Task<int> RunJarvis(string[] args)
{
    string? repoPath = null;
    string? alertPolicyPath = Environment.GetEnvironmentVariable("AECS_ALERT_POLICY");
    string? alertRoot = null;
    var runtimeOptions = new RuntimeCliOptions();
    var invalidArgument = false;

    for (int i = 0; i < args.Length; i++)
    {
        if (args[i] == "--repo" && i + 1 < args.Length)
            repoPath = args[++i];
        else if (args[i] == "--alert-policy" && i + 1 < args.Length)
            alertPolicyPath = args[++i];
        else if (args[i] == "--alert-root" && i + 1 < args.Length)
            alertRoot = args[++i];
        else if (runtimeOptions.TryConsume(args, ref i))
        {
        }
        else
            invalidArgument = true;
    }

    repoPath ??= ".";
    if (invalidArgument)
    {
        Console.WriteLine(
            "Usage: aecs jarvis [--repo <path>] [--alert-policy <policy.json>] " +
            "[--alert-root <path>] " + RuntimeCliOptions.Usage);
        return 1;
    }

    if (!TryCreateRuntime(runtimeOptions, out var createdRuntime))
        return 1;
    using var runtime = createdRuntime;
    PrintEffectiveRuntime(runtime.Configuration, runtimeOptions.ShowEffectiveConfiguration);

    var repl = new JarvisRepl(repoPath, runtime, alertPolicyPath, alertRoot);
    return await repl.RunAsync(CancellationToken.None);
}

static IAgentAdapter BuildExperimentAgent(
    ExperimentRunDefinition definition,
    AecsExecutionRuntime runtime,
    HttpClient httpClient)
{
    var variant = definition.Variant;
    if (variant.Provider == ExperimentProvider.Mock)
    {
        EnsureExperimentParameters(variant);
        return new MockAgentAdapter();
    }

    if (variant.Provider == ExperimentProvider.Local)
    {
        EnsureExperimentParameters(variant, "baseUrl", "contextWindowTokens");
        var baseUrl = Parameter(variant, "baseUrl") ??
            runtime.Configuration.OllamaBaseUrl.Value;
        var contextWindow = IntParameter(
            variant,
            "contextWindowTokens",
            runtime.Configuration.OllamaContextWindowTokens.Value,
            minimum: 1024);
        return new OllamaAdapter(
            httpClient,
            baseUrl,
            contextWindow,
            definition.EffectiveSeed);
    }

    EnsureExperimentParameters(
        variant,
        "baseUrl",
        "contextWindowTokens",
        "maxOutputTokens",
        "temperature");
    var key = runtime.CloudApiKey;
    if (string.IsNullOrWhiteSpace(key))
    {
        throw new InvalidOperationException(
            $"Cloud variant '{variant.Id}' requires a provider API key.");
    }
    var temperature = DoubleParameter(variant, "temperature", 0.2, 0, 2);
    return new CloudAdapter(httpClient, new CloudAdapterOptions
    {
        ApiKey = key,
        Model = variant.Model,
        BaseUrl = Parameter(variant, "baseUrl")
            ?? runtime.Configuration.CloudBaseUrl.Value,
        ContextWindowTokens = IntParameter(
            variant,
            "contextWindowTokens",
            runtime.Configuration.CloudContextWindowTokens.Value,
            minimum: 1024),
        MaxTokens = IntParameter(
            variant,
            "maxOutputTokens",
            runtime.Configuration.CloudMaxOutputTokens.Value,
            minimum: 1),
        Temperature = temperature,
        Seed = definition.EffectiveSeed
    });
}

static void EnsureExperimentParameters(
    ExperimentVariantDefinition variant,
    params string[] supported)
{
    var supportedSet = supported.ToHashSet(StringComparer.Ordinal);
    var unknown = variant.Parameters.Keys.Where(key => !supportedSet.Contains(key)).ToList();
    if (unknown.Count > 0)
    {
        throw new InvalidOperationException(
            $"Variant '{variant.Id}' has unsupported parameters: " +
            string.Join(", ", unknown));
    }
}

static string? Parameter(ExperimentVariantDefinition variant, string name) =>
    variant.Parameters.TryGetValue(name, out var value) ? value : null;

static int IntParameter(
    ExperimentVariantDefinition variant,
    string name,
    int defaultValue,
    int minimum)
{
    var value = Parameter(variant, name);
    if (value is null)
        return defaultValue;
    if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var parsed) ||
        parsed < minimum)
    {
        throw new InvalidOperationException(
            $"Variant '{variant.Id}' parameter '{name}' is invalid.");
    }
    return parsed;
}

static double DoubleParameter(
    ExperimentVariantDefinition variant,
    string name,
    double defaultValue,
    double minimum,
    double maximum)
{
    var value = Parameter(variant, name);
    if (value is null)
        return defaultValue;
    if (!double.TryParse(
            value,
            NumberStyles.Float,
            CultureInfo.InvariantCulture,
            out var parsed) || parsed < minimum || parsed > maximum)
    {
        throw new InvalidOperationException(
            $"Variant '{variant.Id}' parameter '{name}' is invalid.");
    }
    return parsed;
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

        if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value) &&
            string.IsNullOrEmpty(Environment.GetEnvironmentVariable(key)))
        {
            Environment.SetEnvironmentVariable(key, value);
        }
    }
}

internal enum DoctorStatus
{
    Ready,
    DependencyAbsent,
    ConfigurationInvalid,
    ServiceUnavailable
}

internal sealed record DoctorCheck(
    string Id,
    DoctorStatus Status,
    bool Required,
    string Message)
{
    public static DoctorCheck Ready(string id, string message, bool required = true) =>
        new(id, DoctorStatus.Ready, required, message);

    public static DoctorCheck DependencyAbsent(
        string id,
        string message,
        bool required = true) =>
        new(id, DoctorStatus.DependencyAbsent, required, message);

    public static DoctorCheck ConfigurationInvalid(
        string id,
        string message,
        bool required = true) =>
        new(id, DoctorStatus.ConfigurationInvalid, required, message);

    public static DoctorCheck ServiceUnavailable(
        string id,
        string message,
        bool required = true) =>
        new(id, DoctorStatus.ServiceUnavailable, required, message);
}

internal sealed record DiagnosticProcessResult(
    bool Started,
    bool Succeeded,
    int ExitCode,
    string StandardOutput,
    string StandardError,
    bool TimedOut)
{
    public static DiagnosticProcessResult NotStarted() =>
        new(false, false, -1, string.Empty, string.Empty, TimedOut: false);
}
