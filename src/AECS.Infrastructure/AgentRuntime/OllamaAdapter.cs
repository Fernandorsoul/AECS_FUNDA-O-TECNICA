using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.AgentRuntime;

public class OllamaAdapter : IAgentAdapter
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly int _contextWindowTokens;
    private readonly int? _seed;
    private readonly LocalComputeCostPolicy _localCostPolicy;

    public OllamaAdapter(
        HttpClient httpClient,
        string baseUrl = "http://localhost:11434",
        int contextWindowTokens = 32_768,
        int? seed = null,
        LocalComputeCostPolicy? localCostPolicy = null)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _contextWindowTokens = contextWindowTokens;
        _seed = seed;
        _localCostPolicy = localCostPolicy ?? new LocalComputeCostPolicy();
        _localCostPolicy.Validate();
    }

    public AgentContextProfile GetContextProfile(AgentExecutionRequest request) =>
        AgentContextProfile.Conservative(
            nameof(OllamaAdapter),
            request.Model,
            request.Budget,
            ConservativeTokenCounter.Count(BuildPrompt(request.WithoutContext())) + 4,
            contextWindowTokens: _contextWindowTokens);

    public async Task<AgentRunResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var prompt = BuildPrompt(request);
            var model = request.Model;
            var estimatedInputTokens = ConservativeTokenCounter.Count(prompt);
            var profile = GetContextProfile(request);
            var maximumOutputTokens = Math.Min(
                profile.ReservedOutputTokens,
                profile.EffectiveTotalTokens(request.Budget) - estimatedInputTokens);
            if (maximumOutputTokens <= 0)
            {
                var duration = stopwatch.Elapsed;
                var accounting = Accounting(
                    request.Model,
                    estimatedInputTokens,
                    0,
                    providerInputTokens: null,
                    providerOutputTokens: null,
                    duration);
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = "No token budget remains for an Ollama request",
                    ExitCode = -1,
                    Duration = duration,
                    EstimatedCost = accounting.LocalResourceEstimatedCostUsd ?? 0m,
                    UsageAccounting = accounting,
                    ExitReason = "BudgetExceeded",
                    FailureKind = AgentFailureKind.BudgetExceeded
                };
            }

            var requestBody = new OllamaRequest
            {
                Model = model,
                Prompt = prompt,
                Stream = false,
                Options = new OllamaOptions
                {
                    NumCtx = _contextWindowTokens,
                    NumPredict = maximumOutputTokens,
                    Seed = _seed
                }
            };

            var json = JsonSerializer.Serialize(requestBody, OllamaJsonContext.Default.OllamaRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                var duration = stopwatch.Elapsed;
                var accounting = Accounting(
                    request.Model,
                    estimatedInputTokens,
                    maximumOutputTokens,
                    providerInputTokens: null,
                    providerOutputTokens: null,
                    duration);
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = $"Ollama API error {(int)response.StatusCode}: {errorBody}",
                    ExitCode = (int)response.StatusCode,
                    Duration = duration,
                    EstimatedCost = accounting.LocalResourceEstimatedCostUsd ?? 0m,
                    UsageAccounting = accounting,
                    ExitReason = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? "RateLimited"
                        : "ApiError",
                    FailureKind = ClassifyStatusCode((int)response.StatusCode),
                    RetryAfter = GetRetryAfter(response)
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var ollamaResponse = JsonSerializer.Deserialize(responseJson, OllamaJsonContext.Default.OllamaResponse);

            stopwatch.Stop();

            if (ollamaResponse is null)
            {
                var accounting = Accounting(
                    request.Model,
                    estimatedInputTokens,
                    maximumOutputTokens,
                    providerInputTokens: null,
                    providerOutputTokens: null,
                    stopwatch.Elapsed);
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = "Failed to deserialize Ollama response",
                    ExitCode = -1,
                    Duration = stopwatch.Elapsed,
                    EstimatedCost = accounting.LocalResourceEstimatedCostUsd ?? 0m,
                    UsageAccounting = accounting,
                    ExitReason = "DeserializationError"
                };
            }

            var filesChanged = ExtractModifiedFiles(ollamaResponse.Response);
            var inputTokens = ollamaResponse.PromptEvalCount ?? estimatedInputTokens;
            var outputTokens = ollamaResponse.EvalCount ??
                ConservativeTokenCounter.Count(ollamaResponse.Response ?? string.Empty);
            var successAccounting = Accounting(
                request.Model,
                estimatedInputTokens,
                maximumOutputTokens,
                ollamaResponse.PromptEvalCount,
                ollamaResponse.EvalCount,
                stopwatch.Elapsed);

            return new AgentRunResult
            {
                Success = true,
                StdOut = ollamaResponse.Response ?? string.Empty,
                StdErr = string.Empty,
                ExitCode = 0,
                Duration = stopwatch.Elapsed,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                EstimatedCost = successAccounting.LocalResourceEstimatedCostUsd ?? 0m,
                UsageAccounting = successAccounting,
                FilesChanged = filesChanged,
                ExitReason = "Completed"
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            var accounting = UnavailableAccounting(request, stopwatch.Elapsed);
            return new AgentRunResult
            {
                Success = false,
                StdErr = "Execution timed out or was cancelled",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                EstimatedCost = accounting.LocalResourceEstimatedCostUsd ?? 0m,
                UsageAccounting = accounting,
                ExitReason = cancellationToken.IsCancellationRequested ? "Cancelled" : "Timeout",
                FailureKind = cancellationToken.IsCancellationRequested
                    ? AgentFailureKind.Cancelled
                    : AgentFailureKind.Timeout
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            var accounting = UnavailableAccounting(request, stopwatch.Elapsed);
            return new AgentRunResult
            {
                Success = false,
                StdErr = $"Failed to connect to Ollama at {_baseUrl}: {ex.Message}",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                EstimatedCost = accounting.LocalResourceEstimatedCostUsd ?? 0m,
                UsageAccounting = accounting,
                ExitReason = "ConnectionError",
                FailureKind = AgentFailureKind.Transient
            };
        }
    }

    private static string BuildPrompt(AgentExecutionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a C# developer. Your task is to fix or modify code files.");
        sb.AppendLine();
        sb.AppendLine("STEP-BY-STEP INSTRUCTIONS:");
        sb.AppendLine("1. Read the task and existing code carefully.");
        sb.AppendLine("2. Identify ALL files that need changes (source AND tests).");
        sb.AppendLine("3. Write the COMPLETE new content for each file.");
        sb.AppendLine("4. Output each file using the FILE: format below.");
        sb.AppendLine();
        sb.AppendLine("CRITICAL RULES:");
        sb.AppendLine("- using directives MUST be OUTSIDE and BEFORE namespace");
        sb.AppendLine("- NO comments expressing uncertainty");
        sb.AppendLine("- ALWAYS update test files when changing behavior");
        sb.AppendLine("- Write COMPLETE, COMPILABLE code");
        sb.AppendLine();
        sb.AppendLine("OUTPUT FORMAT (use EXACTLY this format):");
        sb.AppendLine();
        sb.AppendLine("FILE: path/to/File.cs");
        sb.AppendLine("```csharp");
        sb.AppendLine("// complete file content here");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"TASK: {request.Objective}");
        sb.AppendLine();

        if (request.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("ACCEPTANCE CRITERIA:");
            foreach (var criterion in request.AcceptanceCriteria)
            {
                sb.AppendLine($"- {criterion}");
            }
            sb.AppendLine();
        }

        if (!string.IsNullOrWhiteSpace(request.ContextPrompt))
        {
            sb.AppendLine(request.ContextPrompt);
        }
        // Compatibility path for callers that still provide the dictionary only.
        else if (request.CodeContext.Count > 0)
        {
            sb.AppendLine("EXISTING CODE (for reference):");
            sb.AppendLine();
            foreach (var (filePath, content) in request.CodeContext)
            {
                sb.AppendLine($"--- {filePath} ---");
                sb.AppendLine(content);
                sb.AppendLine();
            }
        }

        if (request.Scope.Allowed.Count > 0)
        {
            sb.AppendLine("ALLOWED FILES (only modify these):");
            foreach (var path in request.Scope.Allowed)
            {
                sb.AppendLine($"- {path}");
            }
            sb.AppendLine();
        }

        if (request.Scope.Forbidden.Count > 0)
        {
            sb.AppendLine("FORBIDDEN FILES (do NOT modify these):");
            foreach (var path in request.Scope.Forbidden)
            {
                sb.AppendLine($"- {path}");
            }
            sb.AppendLine();
        }

        sb.AppendLine("Now output the modified files:");

        return sb.ToString();
    }

    private AgentUsageAccounting UnavailableAccounting(
        AgentExecutionRequest request,
        TimeSpan duration) => Accounting(
            request.Model,
            ConservativeTokenCounter.Count(BuildPrompt(request)),
            reservedOutputTokens: null,
            providerInputTokens: null,
            providerOutputTokens: null,
            duration);

    private AgentUsageAccounting Accounting(
        string model,
        int estimatedInputTokens,
        int? reservedOutputTokens,
        int? providerInputTokens,
        int? providerOutputTokens,
        TimeSpan duration) => new()
        {
            Adapter = nameof(OllamaAdapter),
            Model = model,
            EstimatedInputTokens = estimatedInputTokens,
            ReservedOutputTokens = reservedOutputTokens,
            ProviderInputTokens = providerInputTokens,
            ProviderOutputTokens = providerOutputTokens,
            LocalResourceEstimatedCostUsd = _localCostPolicy.Estimate(duration),
            CostComplete = true,
            LocalCostPolicyVersion = _localCostPolicy.Version,
            LocalPowerWatts = _localCostPolicy.PowerWatts,
            LocalElectricityUsdPerKwh = _localCostPolicy.ElectricityUsdPerKwh,
            LocalHardwareCostUsd = _localCostPolicy.HardwareCostUsd,
            LocalHardwareLifetimeHours = _localCostPolicy.HardwareLifetimeHours
        };

    private static List<string> ExtractModifiedFiles(string? response)
    {
        if (string.IsNullOrEmpty(response))
            return [];

        var files = new List<string>();
        var lines = response.Split('\n');

        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = trimmed[5..].Trim();
                if (!string.IsNullOrEmpty(filePath))
                    files.Add(filePath);
            }
        }

        // If no FILE: markers found, try to extract from code block headers
        if (files.Count == 0)
        {
            // Look for file paths in the response text
            var pathRegex = new System.Text.RegularExpressions.Regex(
                @"(?:src|tests?|lib)/[\w/]+\.\w+");

            foreach (Match match in pathRegex.Matches(response))
            {
                if (!files.Contains(match.Value))
                    files.Add(match.Value);
            }
        }

        return files;
    }

    private static AgentFailureKind ClassifyStatusCode(int statusCode) => statusCode switch
    {
        429 => AgentFailureKind.RateLimited,
        408 => AgentFailureKind.Timeout,
        >= 500 and <= 599 => AgentFailureKind.Transient,
        _ => AgentFailureKind.Permanent
    };

    private static TimeSpan? GetRetryAfter(HttpResponseMessage response)
    {
        var retryAfter = response.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return delta > TimeSpan.Zero ? delta : TimeSpan.Zero;
        if (retryAfter?.Date is { } date)
        {
            var delay = date - DateTimeOffset.UtcNow;
            return delay > TimeSpan.Zero ? delay : TimeSpan.Zero;
        }

        return null;
    }
}

internal class OllamaRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("prompt")]
    public string Prompt { get; set; } = string.Empty;

    [JsonPropertyName("stream")]
    public bool Stream { get; set; }

    [JsonPropertyName("options")]
    public OllamaOptions Options { get; set; } = new();
}

internal class OllamaOptions
{
    [JsonPropertyName("num_ctx")]
    public int NumCtx { get; set; }

    [JsonPropertyName("num_predict")]
    public int NumPredict { get; set; }

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; set; }
}

internal class OllamaResponse
{
    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int? PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int? EvalCount { get; set; }

    [JsonPropertyName("total_duration")]
    public long TotalDuration { get; set; }
}

[JsonSerializable(typeof(OllamaRequest))]
[JsonSerializable(typeof(OllamaResponse))]
internal partial class OllamaJsonContext : JsonSerializerContext;
