using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IExecutionController
{
    Task<ExecutionPlan> PlanAsync(TaskContract task, CancellationToken cancellationToken);
}
