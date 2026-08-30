using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public class FinalDecisionRecord
{
    public TaskDecision Decision { get; init; }
    public TaskState State { get; init; }
    public string Reason { get; init; } = string.Empty;
    public List<string> RequiredVerifiers { get; init; } = [];
    public DateTime DecidedAt { get; init; } = DateTime.UtcNow;
}

public class ExecutionEvidence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public TaskContract TaskContract { get; init; } = new();
    public AgentRun AgentRun { get; init; } = new();
    public AgentRunResult AgentResult { get; init; } = new();
    public BaselineSnapshot Baseline { get; init; } = new();
    public List<VerificationResult> BaselineVerificationResults { get; init; } = [];
    public List<ExecutionCommandEvidence> BaselineCommands { get; init; } = [];
    public CandidateChangeSet CandidateChangeSet { get; init; } = new();
    public List<VerificationResult> VerificationResults { get; init; } = [];
    public List<ExecutionCommandEvidence> CandidateCommands { get; init; } = [];
    public FinalDecisionRecord FinalDecision { get; init; } = new();
    public List<string> StateTransitions { get; init; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
