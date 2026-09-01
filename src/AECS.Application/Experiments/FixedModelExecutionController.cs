using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Experiments;

public sealed class FixedModelExecutionController : IExecutionController
{
    private readonly string _model;

    public FixedModelExecutionController(string model)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        _model = model;
    }

    public Task<ExecutionPlan> PlanAsync(
        TaskContract task,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(new ExecutionPlan
        {
            TaskId = task.Id,
            Model = _model,
            Budget = task.Budget,
            Risk = task.Constraints.SecurityRisk,
            Verification = task.Verification,
            Capabilities = task.Execution.EffectiveCapabilities
        });
    }
}
