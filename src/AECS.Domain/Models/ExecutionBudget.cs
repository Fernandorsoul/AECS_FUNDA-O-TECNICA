namespace AECS.Domain.Models;

public class ExecutionBudget
{
    public int MaxTokens { get; init; }
    public decimal MaxCostUsd { get; init; }
    public int MaxRetries { get; init; }
    public int MaxDurationSeconds { get; init; }
    public int MaxFilesChanged { get; init; }

    public static ExecutionBudget Default => new()
    {
        MaxTokens = 60000,
        MaxCostUsd = 0.20m,
        MaxRetries = 1,
        MaxDurationSeconds = 120,
        MaxFilesChanged = 10
    };
}
