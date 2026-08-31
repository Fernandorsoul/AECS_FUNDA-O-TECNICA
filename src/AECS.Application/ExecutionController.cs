using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application;

public class ExecutionController : IExecutionController
{
    private static readonly Dictionary<RiskLevel, string> ModelByRisk = new()
    {
        [RiskLevel.R0] = "qwen2.5-coder:7b",
        [RiskLevel.R1] = "qwen2.5-coder:7b",
        [RiskLevel.R2] = "qwen2.5-coder:14b",
        [RiskLevel.R3] = "qwen2.5-coder:14b",
        [RiskLevel.R4] = "qwen2.5-coder:14b" // R4 always uses cloud fallback
    };

    public Task<ExecutionPlan> PlanAsync(TaskContract task, CancellationToken cancellationToken)
    {
        var risk = task.Constraints.SecurityRisk;
        var model = ModelByRisk.GetValueOrDefault(risk, "codellama:3b");

        var plan = new ExecutionPlan
        {
            TaskId = task.Id,
            Model = model,
            Budget = task.Budget,
            Risk = risk,
            Verification = task.Verification,
            Capabilities = task.Execution.EffectiveCapabilities
        };

        return Task.FromResult(plan);
    }
}
