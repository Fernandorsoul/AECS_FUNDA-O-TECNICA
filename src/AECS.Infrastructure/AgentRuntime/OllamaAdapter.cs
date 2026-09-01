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

    public OllamaAdapter(
        HttpClient httpClient,
        string baseUrl = "http://localhost:11434",
        int contextWindowTokens = 32_768)
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
        _contextWindowTokens = contextWindowTokens;
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
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = "No token budget remains for an Ollama request",
                    ExitCode = -1,
                    Duration = stopwatch.Elapsed,
                    ExitReason = "BudgetExceeded",
                    FailureKind = AgentFailureKind.BudgetExceeded
                };
            }

            var requestBody = new OllamaRequest
            {
                Model = model,
                Prompt = prompt,
                Stream = false,
                Options = new OllamaOptions { NumPredict = maximumOutputTokens }
            };

            var json = JsonSerializer.Serialize(requestBody, OllamaJsonContext.Default.OllamaRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content, cancellationToken);
            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = $"Ollama API error {(int)response.StatusCode}: {errorBody}",
                    ExitCode = (int)response.StatusCode,
                    Duration = stopwatch.Elapsed,
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
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = "Failed to deserialize Ollama response",
                    ExitCode = -1,
                    Duration = stopwatch.Elapsed,
                    ExitReason = "DeserializationError"
                };
            }

            var filesChanged = ExtractModifiedFiles(ollamaResponse.Response);

            return new AgentRunResult
            {
                Success = true,
                StdOut = ollamaResponse.Response ?? string.Empty,
                StdErr = string.Empty,
                ExitCode = 0,
                Duration = stopwatch.Elapsed,
                InputTokens = ollamaResponse.PromptEvalCount,
                OutputTokens = ollamaResponse.EvalCount,
                EstimatedCost = 0m, // Local LLM = $0
                FilesChanged = filesChanged,
                ExitReason = "Completed"
            };
        }
        catch (OperationCanceledException)
        {
            stopwatch.Stop();
            return new AgentRunResult
            {
                Success = false,
                StdErr = "Execution timed out or was cancelled",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                ExitReason = cancellationToken.IsCancellationRequested ? "Cancelled" : "Timeout",
                FailureKind = cancellationToken.IsCancellationRequested
                    ? AgentFailureKind.Cancelled
                    : AgentFailureKind.Timeout
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new AgentRunResult
            {
                Success = false,
                StdErr = $"Failed to connect to Ollama at {_baseUrl}: {ex.Message}",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                ExitReason = "ConnectionError",
                FailureKind = AgentFailureKind.Transient
            };
        }
    }

    private static string BuildPrompt(AgentExecutionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a C# developer. Your task is to fix or modify code files.");
        sb.AppendLine("You MUST output each modified file using EXACTLY this format:");
        sb.AppendLine();
        sb.AppendLine("FILE: src/Path/To/File.cs");
        sb.AppendLine("```csharp");
        sb.AppendLine("// full file content here");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine("IMPORTANT: Always start each file with 'FILE:' followed by the path.");
        sb.AppendLine("Do NOT include explanations. Output ONLY the FILE blocks.");
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
            var codeBlockRegex = new System.Text.RegularExpressions.Regex(
                @"```\w*\s*\n(.*?)\n```",
                System.Text.RegularExpressions.RegexOptions.Singleline);

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
    [JsonPropertyName("num_predict")]
    public int NumPredict { get; set; }
}

internal class OllamaResponse
{
    [JsonPropertyName("response")]
    public string? Response { get; set; }

    [JsonPropertyName("prompt_eval_count")]
    public int PromptEvalCount { get; set; }

    [JsonPropertyName("eval_count")]
    public int EvalCount { get; set; }

    [JsonPropertyName("total_duration")]
    public long TotalDuration { get; set; }
}

[JsonSerializable(typeof(OllamaRequest))]
[JsonSerializable(typeof(OllamaResponse))]
internal partial class OllamaJsonContext : JsonSerializerContext;
