namespace AECS.Domain.Models;

using System.Text.Json.Serialization;

public class AgentRunResult
{
    public bool Success { get; init; }
    public string StdOut { get; init; } = string.Empty;
    public string StdErr { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public TimeSpan Duration { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentUsageAccounting? UsageAccounting { get; init; }
    public List<string> FilesChanged { get; init; } = [];
    public string ExitReason { get; init; } = string.Empty;
    public AgentFailureKind FailureKind { get; init; }
    public TimeSpan? RetryAfter { get; init; }
}
