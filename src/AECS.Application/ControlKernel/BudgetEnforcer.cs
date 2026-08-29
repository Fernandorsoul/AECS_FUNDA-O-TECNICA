using AECS.Domain.Models;

namespace AECS.Application.ControlKernel;

public class BudgetViolation
{
    public string LimitType { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public object? ActualValue { get; init; }
    public object? LimitValue { get; init; }
}

public class BudgetEnforcer
{
    public BudgetViolation? Check(ExecutionBudget budget, AgentRunResult result, int retryCount)
    {
        if (result.InputTokens + result.OutputTokens > budget.MaxTokens)
        {
            return new BudgetViolation
            {
                LimitType = "MaxTokens",
                Message = $"Token limit exceeded: {result.InputTokens + result.OutputTokens} > {budget.MaxTokens}",
                ActualValue = result.InputTokens + result.OutputTokens,
                LimitValue = budget.MaxTokens
            };
        }

        if (result.EstimatedCost > budget.MaxCostUsd)
        {
            return new BudgetViolation
            {
                LimitType = "MaxCostUsd",
                Message = $"Cost limit exceeded: ${result.EstimatedCost:F4} > ${budget.MaxCostUsd:F2}",
                ActualValue = result.EstimatedCost,
                LimitValue = budget.MaxCostUsd
            };
        }

        if (retryCount > budget.MaxRetries)
        {
            return new BudgetViolation
            {
                LimitType = "MaxRetries",
                Message = $"Retry limit exceeded: {retryCount} > {budget.MaxRetries}",
                ActualValue = retryCount,
                LimitValue = budget.MaxRetries
            };
        }

        if (result.Duration.TotalSeconds > budget.MaxDurationSeconds)
        {
            return new BudgetViolation
            {
                LimitType = "MaxDurationSeconds",
                Message = $"Duration limit exceeded: {result.Duration.TotalSeconds:F0}s > {budget.MaxDurationSeconds}s",
                ActualValue = (int)result.Duration.TotalSeconds,
                LimitValue = budget.MaxDurationSeconds
            };
        }

        if (result.FilesChanged.Count > budget.MaxFilesChanged)
        {
            return new BudgetViolation
            {
                LimitType = "MaxFilesChanged",
                Message = $"Files changed limit exceeded: {result.FilesChanged.Count} > {budget.MaxFilesChanged}",
                ActualValue = result.FilesChanged.Count,
                LimitValue = budget.MaxFilesChanged
            };
        }

        return null;
    }
}
