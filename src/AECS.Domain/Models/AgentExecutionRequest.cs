using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public class AgentExecutionRequest
{
    public string TaskId { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public List<string> AcceptanceCriteria { get; init; } = [];
    public string RepoPath { get; init; } = string.Empty;
    public ScopeDefinition Scope { get; init; } = new();
    public ExecutionBudget Budget { get; init; } = ExecutionBudget.Default;
    public RiskLevel Risk { get; init; }
    public string Model { get; init; } = string.Empty;
    public Dictionary<string, string> CodeContext { get; init; } = new();
    public string ContextPrompt { get; init; } = string.Empty;
}
