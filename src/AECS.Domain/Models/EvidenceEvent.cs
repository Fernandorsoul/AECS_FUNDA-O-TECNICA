namespace AECS.Domain.Models;

public class EvidenceEvent
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string TaskId { get; init; } = string.Empty;
    public Guid? AgentRunId { get; init; }
    public string EventType { get; init; } = string.Empty;
    public string Payload { get; init; } = "{}";
    public string Authority { get; init; } = "deterministic";
    public DateTime OccurredAt { get; init; } = DateTime.UtcNow;
}
