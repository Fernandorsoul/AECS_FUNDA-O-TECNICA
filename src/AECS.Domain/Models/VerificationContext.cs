namespace AECS.Domain.Models;

public class VerificationContext
{
    public string TaskId { get; init; } = string.Empty;
    public string AgentRunId { get; init; } = string.Empty;
    public string RepoPath { get; init; } = string.Empty;
    public TaskContract Contract { get; init; } = new();
    public AgentRunResult AgentResult { get; init; } = new();
}
