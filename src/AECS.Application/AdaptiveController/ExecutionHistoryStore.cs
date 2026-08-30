using AECS.Domain.Enums;

namespace AECS.Application.AdaptiveController;

public class ExecutionRecord
{
    public string TaskId { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; }
    public string Model { get; init; } = string.Empty;
    public TaskDecision Decision { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal Cost { get; init; }
    public TimeSpan Duration { get; init; }
    public int FilesChanged { get; init; }
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;
}

public class ModelStats
{
    public string Model { get; init; } = string.Empty;
    public int TotalRuns { get; init; }
    public int SuccessfulRuns { get; init; }
    public double SuccessRate => TotalRuns > 0 ? (double)SuccessfulRuns / TotalRuns : 0;
    public decimal AverageCost { get; init; }
    public int AverageTokens { get; init; }
}

public class ExecutionHistoryStore
{
    private readonly List<ExecutionRecord> _records = [];

    public void Add(ExecutionRecord record)
    {
        _records.Add(record);
    }

    public void AddRange(IEnumerable<ExecutionRecord> records)
    {
        _records.AddRange(records);
    }

    public IReadOnlyList<ExecutionRecord> GetAll() => _records.AsReadOnly();

    public IReadOnlyList<ExecutionRecord> GetByRisk(RiskLevel risk)
    {
        return _records.Where(r => r.Risk == risk).ToList().AsReadOnly();
    }

    public IReadOnlyList<ExecutionRecord> GetByModel(string model)
    {
        return _records.Where(r => r.Model == model).ToList().AsReadOnly();
    }

    public IReadOnlyList<ExecutionRecord> GetByTaskType(string objective)
    {
        var keywords = objective.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            .Where(w => w.Length > 3)
            .Select(w => w.ToLowerInvariant())
            .ToList();

        return _records
            .Where(r => keywords.Any(k => r.Objective.Contains(k, StringComparison.OrdinalIgnoreCase)))
            .ToList()
            .AsReadOnly();
    }

    public List<ModelStats> GetModelStats()
    {
        return _records
            .GroupBy(r => r.Model)
            .Select(g => new ModelStats
            {
                Model = g.Key,
                TotalRuns = g.Count(),
                SuccessfulRuns = g.Count(r => r.Decision == TaskDecision.Verified),
                AverageCost = g.Average(r => r.Cost),
                AverageTokens = (int)g.Average(r => r.InputTokens + r.OutputTokens)
            })
            .ToList();
    }

    public int Count => _records.Count;
}
