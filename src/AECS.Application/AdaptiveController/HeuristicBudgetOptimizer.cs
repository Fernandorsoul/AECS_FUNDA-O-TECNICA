using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application.AdaptiveController;

public class HeuristicBudgetOptimizer
{
    private readonly ExecutionHistoryStore _history;
    private readonly int _minSamples;

    public HeuristicBudgetOptimizer(ExecutionHistoryStore history, int minSamples = 3)
    {
        _history = history;
        _minSamples = minSamples;
    }

    public ExecutionBudget Optimize(RiskLevel risk, ExecutionBudget requested)
    {
        var riskRecords = _history.GetByRisk(risk);

        // Not enough data — return requested budget
        if (riskRecords.Count < _minSamples)
            return requested;

        var successfulRecords = riskRecords
            .Where(r => r.Decision == TaskDecision.Verified)
            .ToList();

        if (successfulRecords.Count < 2)
            return requested;

        // Calculate p75 of actual usage for successful runs
        var tokenUsages = successfulRecords
            .Select(r => r.InputTokens + r.OutputTokens)
            .OrderBy(t => t)
            .ToList();
        var p75Tokens = tokenUsages[(int)(tokenUsages.Count * 0.75)];

        var durations = successfulRecords
            .Select(r => (int)r.Duration.TotalSeconds)
            .OrderBy(d => d)
            .ToList();
        var p75Duration = durations[(int)(durations.Count * 0.75)];

        // Use the larger of: requested or p75 * 1.5 (safety margin)
        return new ExecutionBudget
        {
            MaxTokens = Math.Max(requested.MaxTokens, (int)(p75Tokens * 1.5)),
            MaxCostUsd = requested.MaxCostUsd, // Don't optimize cost limit
            MaxRetries = requested.MaxRetries,
            MaxDurationSeconds = Math.Max(requested.MaxDurationSeconds, (int)(p75Duration * 1.5)),
            MaxFilesChanged = requested.MaxFilesChanged
        };
    }
}
