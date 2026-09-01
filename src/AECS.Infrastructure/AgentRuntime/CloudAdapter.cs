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
    public int ContextWindowTokens { get; set; } = 32_768;
    public double Temperature { get; set; } = 0.2;
    public int? Seed { get; set; }
}

public class CloudAdapter : IAgentAdapter
{
    private const string SystemPrompt =
        "You are a C# developer. Output only FILE blocks with modified code. No explanations.";
    private const int ChatMessageOverheadTokens = 16;
    private readonly HttpClient _httpClient;
    private readonly CloudAdapterOptions _options;
    private readonly ProviderPricingCatalog _pricing;

    public CloudAdapter(
        HttpClient httpClient,
        CloudAdapterOptions options,
        ProviderPricingCatalog? pricing = null)
    {
        _httpClient = httpClient;
        _options = options;
        _pricing = pricing ?? ProviderPricingCatalog.Current;
    }

    public AgentContextProfile GetContextProfile(AgentExecutionRequest request) =>
        AgentContextProfile.Conservative(
            nameof(CloudAdapter),
            _options.Model,
            request.Budget,
            ConservativeTokenCounter.Count(
                SystemPrompt + "\n" + BuildPrompt(request.WithoutContext())) +
            ChatMessageOverheadTokens,
            contextWindowTokens: _options.ContextWindowTokens,
            desiredOutputTokens: _options.MaxTokens);

    public async Task<AgentRunResult> ExecuteAsync(
        AgentExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            var prompt = BuildPrompt(request);
            var model = _options.Model; // Always use cloud model, not local model name
            var rate = _pricing.Resolve(model);
            var estimatedInputTokens = EstimateInputTokens(prompt);
            var maxOutputTokens = GetMaximumOutputTokens(
                rate,
                estimatedInputTokens,
                request.Budget,
                _options.MaxTokens,
                _options.ContextWindowTokens);
            if (maxOutputTokens <= 0)
            {
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = "No token or cost budget remains for a cloud request",
                    ExitCode = -1,
                    Duration = stopwatch.Elapsed,
                    UsageAccounting = Accounting(
                        model,
                        rate,
                        estimatedInputTokens,
                        0,
                        providerInputTokens: null,
                        providerOutputTokens: null,
                        providerRequestId: string.Empty,
                        rateCardCost: 0m,
                        costComplete: true,
                        usageBasis: "no-provider-request"),
                    ExitReason = "BudgetExceeded",
                    FailureKind = AgentFailureKind.BudgetExceeded
                };
            }

            var requestBody = new CloudRequest
            {
                Model = model,
                Messages =
                [
                    new ChatMessage { Role = "system", Content = SystemPrompt },
                    new ChatMessage { Role = "user", Content = prompt }
                ],
                MaxTokens = maxOutputTokens,
                Temperature = _options.Temperature,
                Seed = _options.Seed
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
                    UsageAccounting = Accounting(
                        model,
                        rate,
                        estimatedInputTokens,
                        maxOutputTokens,
                        providerInputTokens: null,
                        providerOutputTokens: null,
                        providerRequestId: string.Empty,
                        rateCardCost: null,
                        costComplete: false,
                        usageBasis: "unavailable"),
                    ExitReason = response.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                        ? "RateLimited"
                        : "ApiError",
                    FailureKind = ClassifyStatusCode((int)response.StatusCode),
                    RetryAfter = GetRetryAfter(response)
                };
            }

            var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
            var cloudResponse = JsonSerializer.Deserialize(responseJson, CloudJsonContext.Default.CloudResponse);

            stopwatch.Stop();

            if (cloudResponse?.Choices is null || cloudResponse.Choices.Count == 0)
            {
                var emptyInputTokens = cloudResponse?.Usage?.PromptTokens;
                var emptyOutputTokens = cloudResponse?.Usage?.CompletionTokens;
                var emptyCost = emptyInputTokens is not null && emptyOutputTokens is not null
                    ? _pricing.Estimate(
                        rate,
                        emptyInputTokens.Value,
                        emptyOutputTokens.Value)
                    : (decimal?)null;
                return new AgentRunResult
                {
                    Success = false,
                    StdErr = "Empty response from cloud API",
                    ExitCode = -1,
                    Duration = stopwatch.Elapsed,
                    InputTokens = emptyInputTokens ?? 0,
                    OutputTokens = emptyOutputTokens ?? 0,
                    EstimatedCost = emptyCost ?? 0m,
                    UsageAccounting = Accounting(
                        model,
                        rate,
                        estimatedInputTokens,
                        maxOutputTokens,
                        emptyInputTokens,
                        emptyOutputTokens,
                        cloudResponse?.Id ?? string.Empty,
                        rateCardCost: emptyCost,
                        costComplete: emptyCost is not null,
                        usageBasis: emptyInputTokens is not null && emptyOutputTokens is not null
                            ? "provider-reported"
                            : emptyInputTokens is not null || emptyOutputTokens is not null
                                ? "partial-provider-reported"
                                : "unavailable"),
                    ExitReason = "EmptyResponse"
                };
            }

            var responseText = cloudResponse.Choices[0].Message?.Content ?? string.Empty;
            var filesChanged = ExtractModifiedFiles(responseText);

            var providerInputTokens = cloudResponse.Usage?.PromptTokens;
            var providerOutputTokens = cloudResponse.Usage?.CompletionTokens;
            var inputTokens = cloudResponse.Usage?.PromptTokens ?? estimatedInputTokens;
            var outputTokens = cloudResponse.Usage?.CompletionTokens ??
                ConservativeTokenCounter.Count(responseText);
            var estimatedCost = _pricing.Estimate(rate, inputTokens, outputTokens);

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
                UsageAccounting = Accounting(
                    model,
                    rate,
                    estimatedInputTokens,
                    maxOutputTokens,
                    providerInputTokens,
                    providerOutputTokens,
                    cloudResponse.Id,
                    estimatedCost,
                    costComplete: true,
                    usageBasis: UsageBasis(providerInputTokens, providerOutputTokens)),
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
                UsageAccounting = UnavailableAccounting(request),
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
                StdErr = $"Cloud API error: {ex.Message}",
                ExitCode = -1,
                Duration = stopwatch.Elapsed,
                UsageAccounting = UnavailableAccounting(request),
                ExitReason = "ApiError",
                FailureKind = AgentFailureKind.Transient
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

        if (!string.IsNullOrWhiteSpace(request.ContextPrompt))
        {
            sb.AppendLine(request.ContextPrompt);
        }
        else if (request.CodeContext.Count > 0)
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

    private static int GetMaximumOutputTokens(
        ProviderTokenRate rate,
        int estimatedInputTokens,
        ExecutionBudget budget,
        int configuredMaximum,
        int contextWindowTokens)
    {
        if (budget.MaxTokens <= 0 || budget.MaxCostUsd <= 0)
            return 0;

        var availableOutputTokens = Math.Min(budget.MaxTokens, contextWindowTokens) -
            estimatedInputTokens;
        if (availableOutputTokens <= 0)
            return 0;
        var inputCost = estimatedInputTokens * rate.InputUsdPerMillionTokens / 1_000_000;
        var affordableOutputTokens = rate.OutputUsdPerMillionTokens <= 0
            ? budget.MaxTokens
            : (int)Math.Floor(
                Math.Max(0m, budget.MaxCostUsd - inputCost) * 1_000_000 /
                rate.OutputUsdPerMillionTokens);
        return Math.Max(0, Math.Min(
            Math.Min(availableOutputTokens, affordableOutputTokens),
            configuredMaximum));
    }

    private static int EstimateInputTokens(string prompt) =>
        ConservativeTokenCounter.Count(SystemPrompt + "\n" + prompt) +
        ChatMessageOverheadTokens;

    private static string UsageBasis(int? providerInputTokens, int? providerOutputTokens) =>
        providerInputTokens is not null && providerOutputTokens is not null
            ? "provider-reported"
            : providerInputTokens is not null || providerOutputTokens is not null
                ? "provider-reported-with-client-fallback"
                : "client-estimated";

    private AgentUsageAccounting UnavailableAccounting(AgentExecutionRequest request)
    {
        var model = _options.Model;
        var rate = _pricing.Resolve(model);
        return Accounting(
            model,
            rate,
            EstimateInputTokens(BuildPrompt(request)),
            reservedOutputTokens: null,
            providerInputTokens: null,
            providerOutputTokens: null,
            providerRequestId: string.Empty,
            rateCardCost: null,
            costComplete: false,
            usageBasis: "unavailable");
    }

    private AgentUsageAccounting Accounting(
        string model,
        ProviderTokenRate rate,
        int estimatedInputTokens,
        int? reservedOutputTokens,
        int? providerInputTokens,
        int? providerOutputTokens,
        string providerRequestId,
        decimal? rateCardCost,
        bool costComplete,
        string usageBasis) => new()
        {
            Adapter = nameof(CloudAdapter),
            Model = model,
            EstimatedInputTokens = estimatedInputTokens,
            ReservedOutputTokens = reservedOutputTokens,
            ProviderInputTokens = providerInputTokens,
            ProviderOutputTokens = providerOutputTokens,
            ProviderRequestId = providerRequestId,
            RateCardEstimatedCostUsd = rateCardCost,
            CostComplete = costComplete,
            Currency = _pricing.Table.Currency,
            PricingTableVersion = _pricing.Table.Version,
            PricingTableHash = _pricing.Table.Hash,
            PricingEffectiveDate = rate.EffectiveDate,
            PricingSource = rate.Source,
            PricingRateKind = rate.RateKind,
            RateCardUsageBasis = usageBasis
        };

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

    [JsonPropertyName("seed")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public int? Seed { get; set; }
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
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

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
    public int? PromptTokens { get; set; }

    [JsonPropertyName("completion_tokens")]
    public int? CompletionTokens { get; set; }
}

[JsonSerializable(typeof(CloudRequest))]
[JsonSerializable(typeof(CloudResponse))]
internal partial class CloudJsonContext : JsonSerializerContext;
