namespace AECS.Domain.Models;

public class VerificationContext
{
    public string TaskId { get; init; } = string.Empty;
    public string AgentRunId { get; init; } = string.Empty;
    public string RepoPath { get; init; } = string.Empty;
    public TaskContract Contract { get; init; } = new();
    public AgentRunResult AgentResult { get; init; } = new();
    public CandidateChangeSet CandidateChangeSet { get; init; } = new();
    public List<ExecutionCommandEvidence> CommandEvidence { get; init; } = [];
    public string Phase { get; init; } = string.Empty;
    public RepositorySnapshot? BaselineRepositorySnapshot { get; init; }
    public RepositorySnapshot? CandidateRepositorySnapshot { get; init; }
    public CSharpSymbolGraph? BaselineCSharpSymbolGraph { get; init; }
    public CSharpSymbolGraph? CandidateCSharpSymbolGraph { get; init; }
    public TrajectoryEvidence? Trajectory { get; init; }
    public string SemanticAnalysisError { get; init; } = string.Empty;
}
