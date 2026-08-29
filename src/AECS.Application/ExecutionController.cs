using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application;

public class ExecutionController : IExecutionController
{
    private static readonly Dictionary<RiskLevel, string> ModelByRisk = new()
    {
        [RiskLevel.R0] = "deepseek-coder:1.3b",
        [RiskLevel.R1] = "codellama:3b",
        [RiskLevel.R2] = "codellama:7b",
        [RiskLevel.R3] = "codellama:7b"
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
            Verification = task.Verification
        };

        return Task.FromResult(plan);
    }
}
