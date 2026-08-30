namespace AECS.Application.Experiments;

public class ExperimentRun
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;
    public int TotalTasks { get; init; }
    public int VerifiedCount { get; init; }
    public int RejectedCount { get; init; }
    public int HumanReviewCount { get; init; }
    public TimeSpan TotalDuration { get; init; }
    public decimal TotalCost { get; init; }
    public decimal Cpvc { get; init; }
    public double FirstPassRate { get; init; }
    public string ResultsJson { get; init; } = "{}";
}
