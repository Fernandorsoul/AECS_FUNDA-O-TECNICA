using AECS.Domain.Enums;
using System.Text.Json.Serialization;

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
    public List<AgentAttemptEvidence> AgentAttempts { get; init; } = [];
    public ExecutionBudgetEvidence BudgetUsage { get; init; } = new();
    public BaselineSnapshot Baseline { get; init; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositorySnapshot? RepositorySnapshot { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CSharpSymbolGraph? CSharpSymbolGraph { get; init; }
    public List<VerificationResult> BaselineVerificationResults { get; init; } = [];
    public List<ExecutionCommandEvidence> BaselineCommands { get; init; } = [];
    public ContextManifest ContextManifest { get; init; } = new();
    public CandidateChangeSet CandidateChangeSet { get; init; } = new();
    public List<VerificationResult> VerificationResults { get; init; } = [];
    public List<AcceptanceCriterionResult> AcceptanceCriteriaResults { get; init; } = [];
    public List<ExecutionCommandEvidence> CandidateCommands { get; init; } = [];
    public FinalDecisionRecord FinalDecision { get; init; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AdaptiveShadowEvidence? AdaptiveShadow { get; init; }
    public List<CandidatePromotionEvidence> Promotions { get; init; } = [];
    public List<string> StateTransitions { get; init; } = [];
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
