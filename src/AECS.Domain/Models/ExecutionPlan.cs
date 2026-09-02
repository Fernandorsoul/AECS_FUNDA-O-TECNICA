using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public class ExecutionPlan
{
    public string TaskId { get; init; } = string.Empty;
    public string Model { get; init; } = string.Empty;
    public ExecutionBudget Budget { get; init; } = ExecutionBudget.Default;
    public RiskLevel Risk { get; init; }
    public VerificationProfile Verification { get; init; } = new();
    public ExecutionCapabilityPolicy Capabilities { get; init; } =
        ExecutionCapabilityPolicy.RestrictiveDefault();
}
