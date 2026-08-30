namespace AECS.Domain.Models;

public class PolicyDecision
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string AgentRunId { get; init; } = string.Empty;
    public string Policy { get; init; } = string.Empty;
    public string Action { get; init; } = string.Empty;
    public bool Decision { get; init; }
    public string Reason { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
