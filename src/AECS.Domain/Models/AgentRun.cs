namespace AECS.Domain.Models;

public class AgentRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string TaskId { get; init; } = string.Empty;
    public string AgentType { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime? FinishedAt { get; set; }
    public int InputTokens { get; set; }
    public int OutputTokens { get; set; }
    public int CachedTokens { get; set; }
    public decimal EstimatedCost { get; set; }
    public int RetryCount { get; set; }
    public string ExitReason { get; set; } = string.Empty;
    public List<string> FilesChanged { get; set; } = [];
}
