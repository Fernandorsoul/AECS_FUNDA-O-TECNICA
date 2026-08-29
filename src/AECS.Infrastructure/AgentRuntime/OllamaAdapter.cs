using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.AgentRuntime;

public class OllamaAdapter : IAgentAdapter
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;

    public OllamaAdapter(HttpClient httpClient, string baseUrl = "http://localhost:11434")
    {
        _httpClient = httpClient;
        _baseUrl = baseUrl.TrimEnd('/');
    }

    public async Task<AgentRunResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var prompt = BuildPrompt(request);
            var model = request.Model;

            var requestBody = new OllamaRequest
            {
                Model = model,
                Prompt = prompt,
                Stream = false
            };

            var json = JsonSerializer.Serialize(requestBody, OllamaJsonContext.Default.OllamaRequest);
            var content = new StringContent(json, Encoding.UTF8, "application/json");

            var response = await _httpClient.PostAsync($"{_baseUrl}/api/generate", content, cancellationToken);
            response.EnsureSuccessStatusCode();

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
                ExitReason = "Cancelled"
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
                ExitReason = "ConnectionError"
            };
        }
    }

    private static string BuildPrompt(AgentExecutionRequest request)
    {
        var sb = new StringBuilder();
        sb.AppendLine("You are a C# developer. Given the following task, make the necessary code changes.");
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

        sb.AppendLine("Output the modified files with their full content. Use this format:");
        sb.AppendLine("FILE: <filepath>");
        sb.AppendLine("```csharp");
        sb.AppendLine("<content>");
        sb.AppendLine("```");

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

        return files;
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
