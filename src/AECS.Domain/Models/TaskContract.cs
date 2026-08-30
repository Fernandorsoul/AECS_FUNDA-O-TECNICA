using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public class TaskContract
{
    public string Id { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public List<string> AcceptanceCriteria { get; init; } = [];
    public ScopeDefinition Scope { get; init; } = new();
    public TaskConstraints Constraints { get; init; } = new();
    public ExecutionBudget Budget { get; init; } = ExecutionBudget.Default;
    public RepositoryExecutionProfile Execution { get; init; } = new();
    public VerificationProfile Verification { get; init; } = new();
    public ApprovalPolicy Approval { get; init; } = new();
    public TaskState Status { get; set; } = TaskState.Created;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}
