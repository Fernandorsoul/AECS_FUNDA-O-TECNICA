using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.AdaptiveController;

public class AdaptiveController : IExecutionController
{
    private readonly ExecutionHistoryStore _history;
    private readonly HeuristicModelSelector _modelSelector;
    private readonly HeuristicBudgetOptimizer _budgetOptimizer;
    private readonly ExecutionController _fallbackController = new();

    public AdaptiveController(ExecutionHistoryStore history)
    {
        _history = history;
        _modelSelector = new HeuristicModelSelector(history);
        _budgetOptimizer = new HeuristicBudgetOptimizer(history);
    }

    public Task<ExecutionPlan> PlanAsync(TaskContract task, CancellationToken cancellationToken)
    {
        // Not enough data — use fallback
        if (_history.Count < 5)
            return _fallbackController.PlanAsync(task, cancellationToken);

        var risk = task.Constraints.SecurityRisk;

        // Adaptive model selection
        var model = _modelSelector.SelectModel(risk, task.Objective);

        // Adaptive budget optimization
        var budget = _budgetOptimizer.Optimize(risk, task.Budget);

        var plan = new ExecutionPlan
        {
            TaskId = task.Id,
            Model = model,
            Budget = budget,
            Risk = risk,
            Verification = task.Verification,
            Capabilities = task.Execution.EffectiveCapabilities
        };

        return Task.FromResult(plan);
    }
}
