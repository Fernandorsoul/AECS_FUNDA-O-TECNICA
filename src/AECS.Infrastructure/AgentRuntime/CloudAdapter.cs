using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.AgentRuntime;

public class CloudAdapterOptions
{
    public string ApiKey { get; set; } = string.Empty;
    public string BaseUrl { get; set; } = "https://api.openai.com/v1";
    public string Model { get; set; } = "gpt-4o-mini";
    public int MaxTokens { get; set; } = 4096;
    public double Temperature { get; set; } = 0.2;
}

public class CloudAdapter : IAgentAdapter
{
    private readonly HttpClient _httpClient;
    private readonly CloudAdapterOptions _options;

    public CloudAdapter(HttpClient httpClient, CloudAdapterOptions options)
    {
        _httpClient = httpClient;
        _options = options;
    }

    public async Task<AgentRunResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var prompt = BuildPrompt(request);
            var model = _options.Model; // Always use cloud model, not local model name

            var requestBody = new CloudRequest
            {
                Model = model,
                Messages =
                [
                    new ChatMessage { Role = "system", Content = "You are a C# developer. Output only FILE blocks with modified code. No explanations." },
                    new ChatMessage { Role = "user", Content = prompt }
                ],
                MaxTokens = _options.MaxTokens,
                Temperature = _options.Temperature
            };

            var json = JsonSerializer.Serialize(requestBody, CloudJsonContext.Default.CloudRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var requestMsg = new HttpRequestMessage(HttpMethod.Post, $"{_options.BaseUrl}/chat/completions")
            {
                Content = content
            };
            requestMsg.Headers.Add("Authorization", $"Bearer {_options.ApiKey}");

            var response = await _httpClient.SendAsync(requestMsg, cancellationToken);

            if (!response.IsSuccessStatusCode)
            {
                var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = $"Cloud API error {(int)response.StatusCode}: {errorBody}",
                    ExitCode = (int)response.StatusCode,
                    Duration = stopwatch.Elapsed,
                    ExitReason = "ApiError"
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var cloudResponse = JsonSerializer.Deserialize(responseJson, CloudJsonContext.Default.CloudResponse);

            stopwatch.Stop();

            if (cloudResponse?.Choices is null || cloudResponse.Choices.Count == 0)
            {
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = "Empty response from cloud API",
                    ExitCode = -1,
                    Duration = stopwatch.Elapsed,
                    ExitReason = "EmptyResponse"
                };
            }

            var responseText = cloudResponse.Choices[0].Message?.Content ?? string.Empty;
            var filesChanged = ExtractModifiedFiles(responseText);

            var inputTokens = cloudResponse.Usage?.PromptTokens ?? 0;
            var outputTokens = cloudResponse.Usage?.CompletionTokens ?? 0;
            var estimatedCost = EstimateCost(model, inputTokens, outputTokens);

            return new AgentRunResult
            {
                Success = true,
                StdOut = responseText,
                StdErr = string.Empty,
                ExitCode = 0,
                Duration = stopwatch.Elapsed,
                InputTokens = inputTokens,
                OutputTokens = outputTokens,
                EstimatedCost = estimatedCost,
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
                StdErr = "Cloud API call timed out or was cancelled",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                ExitReason = "Cancelled"
            };
        }
        catch (HttpRequestException ex)
        {
            stopwatch.Stop();
            return new AgentRunResult
            {
                Success = false,
                StdErr = $"Cloud API error: {ex.Message}",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                ExitReason = "ApiError"
            };
        }
    }

    private static string BuildPrompt(AgentExecutionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("Your task is to fix or modify C# code files.");
        sb.AppendLine("Output each modified file using EXACTLY this format:");
        sb.AppendLine();
        sb.AppendLine("FILE: src/Path/To/File.cs");
        sb.AppendLine("```csharp");
        sb.AppendLine("// full file content");
        sb.AppendLine("```");
        sb.AppendLine();
        sb.AppendLine($"TASK: {request.Objective}");
        sb.AppendLine();

        if (request.AcceptanceCriteria.Count > 0)
        {
            sb.AppendLine("ACCEPTANCE CRITERIA:");
            foreach (var c in request.AcceptanceCriteria)
                sb.AppendLine($"- {c}");
            sb.AppendLine();
        }

        if (request.CodeContext.Count > 0)
        {
            sb.AppendLine("EXISTING CODE:");
            foreach (var (path, fileContent) in request.CodeContext)
            {
                sb.AppendLine($"--- {path} ---");
                sb.AppendLine(fileContent);
                sb.AppendLine();
            }
        }

        if (request.Scope.Allowed.Count > 0)
        {
            sb.AppendLine("ALLOWED FILES (only modify these):");
            foreach (var p in request.Scope.Allowed)
                sb.AppendLine($"- {p}");
            sb.AppendLine();
        }

        if (request.Scope.Forbidden.Count > 0)
        {
            sb.AppendLine("FORBIDDEN FILES (do NOT modify):");
            foreach (var p in request.Scope.Forbidden)
                sb.AppendLine($"- {p}");
            sb.AppendLine();
        }

        sb.AppendLine("Output the modified files now:");
        return sb.ToString();
    }

    private static List<string> ExtractModifiedFiles(string? response)
    {
        if (string.IsNullOrEmpty(response))
            return [];

        var files = new List<string>();
        foreach (var line in response.Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.StartsWith("FILE:", StringComparison.OrdinalIgnoreCase))
            {
                var filePath = trimmed[5..].Trim();
                if (!string.IsNullOrEmpty(filePath))
                    files.Add(filePath);
            }
        }

        if (files.Count == 0)
        {
            var pathRegex = new Regex(@"(?:src|tests?|lib)/[\w/]+\.\w+");
            foreach (Match match in pathRegex.Matches(response))
            {
                if (!files.Contains(match.Value))
                    files.Add(match.Value);
            }
        }

        return files;
    }

    private static decimal EstimateCost(string model, int inputTokens, int outputTokens)
    {
        // Approximate pricing per 1M tokens
        var (inputPrice, outputPrice) = model.ToLowerInvariant() switch
        {
            var m when m.Contains("gpt-4o-mini") => (0.15m, 0.60m),
            var m when m.Contains("gpt-4o") => (2.50m, 10.00m),
            var m when m.Contains("gpt-4-turbo") => (10.00m, 30.00m),
            var m when m.Contains("claude-3-5-sonnet") => (3.00m, 15.00m),
            var m when m.Contains("claude-3-haiku") => (0.25m, 1.25m),
            _ => (1.00m, 3.00m) // default estimate
        };

        return (inputTokens * inputPrice + outputTokens * outputPrice) / 1_000_000;
    }
}

internal class CloudRequest
{
    [JsonPropertyName("model")]
    public string Model { get; set; } = string.Empty;

    [JsonPropertyName("messages")]
    public List<ChatMessage> Messages { get; set; } = [];

    [JsonPropertyName("max_tokens")]
    public int MaxTokens { get; set; }

    [JsonPropertyName("temperature")]
    public double Temperature { get; set; }
}

internal class ChatMessage
{
    [JsonPropertyName("role")]
    public string Role { get; set; } = string.Empty;

    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;
}

internal class CloudResponse
{
    [JsonPropertyName("choices")]
    public List<Choice>? Choices { get; set; }

    [JsonPropertyName("usage")]
    public UsageInfo? Usage { get; set; }
}

internal class Choice
{
    [JsonPropertyName("message")]
    public ChatMessage? Message { get; set; }
}

internal class UsageInfo
{
    [JsonPropertyName("prompt_tokens")]
    public int PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int CompletionTokens { get; set; }
}

[JsonSerializable(typeof(CloudRequest))]
[JsonSerializable(typeof(CloudResponse))]
internal partial class CloudJsonContext : JsonSerializerContext;
