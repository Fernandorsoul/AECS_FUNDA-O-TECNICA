using AECS.Domain.Enums;

namespace AECS.Application.AdaptiveController;

public class HeuristicModelSelector
{
    private static readonly Dictionary<RiskLevel, string> DefaultModels = new()
    {
        [RiskLevel.R0] = "deepseek-coder:1.3b",
        [RiskLevel.R1] = "codellama:3b",
        [RiskLevel.R2] = "codellama:7b",
        [RiskLevel.R3] = "codellama:7b"
    };

    private readonly ExecutionHistoryStore _history;
    private readonly int _minSamples;

    public HeuristicModelSelector(ExecutionHistoryStore history, int minSamples = 3)
    {
        _history = history;
        _minSamples = minSamples;
    }

    public string SelectModel(RiskLevel risk, string objective)
    {
        var stats = _history.GetModelStats();

        // Not enough data — use defaults
        if (stats.Sum(s => s.TotalRuns) < _minSamples)
            return DefaultModels.GetValueOrDefault(risk, "codellama:3b");

        // Get records for this risk level
        var riskRecords = _history.GetByRisk(risk);
        if (riskRecords.Count < _minSamples)
            return DefaultModels.GetValueOrDefault(risk, "codellama:3b");

        // Find model with best success rate for this risk level
        var bestModel = riskRecords
            .GroupBy(r => r.Model)
            .Where(g => g.Count() >= 2) // Need at least 2 samples per model
            .Select(g => new
            {
                Model = g.Key,
                SuccessRate = (double)g.Count(r => r.Decision == TaskDecision.Verified) / g.Count(),
                AvgCost = g.Average(r => r.Cost)
            })
            .OrderByDescending(x => x.SuccessRate)
            .ThenBy(x => x.AvgCost)
            .FirstOrDefault();

        return bestModel?.Model ?? DefaultModels.GetValueOrDefault(risk, "codellama:3b");
    }
}
