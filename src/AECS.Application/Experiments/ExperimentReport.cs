using AECS.Domain.Enums;
using AECS.Domain.Models;

namespace AECS.Application.Experiments;

public class TaskExperimentResult
{
    public string TaskId { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; }
    public string Model { get; init; } = string.Empty;
    public TaskDecision Decision { get; init; }
    public string DecisionReason { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    public int FilesChanged { get; init; }
    public Dictionary<string, VerificationStatus> Verifications { get; init; } = new();
    public List<AcceptanceCriterionResult> AcceptanceCriteria { get; init; } = [];
    public List<AgentAttemptEvidence> AgentAttempts { get; init; } = [];
    public ExecutionBudgetEvidence BudgetUsage { get; init; } = new();
    public int RetryCount { get; init; }
    public Guid EvidenceId { get; init; }
    public bool OriginalRepositoryUnchanged { get; init; }
}

public class ExperimentReport
{
    public Guid ExperimentId { get; init; } = Guid.NewGuid();
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;
    public List<TaskExperimentResult> Results { get; init; } = [];

    public int TotalTasks => Results.Count;
    public int VerifiedCount => Results.Count(r => r.Decision == TaskDecision.Verified);
    public int RejectedCount => Results.Count(r => r.Decision == TaskDecision.Rejected);
    public int HumanReviewCount => Results.Count(r => r.Decision == TaskDecision.HumanReviewRequired);
    public TimeSpan TotalDuration => Results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration);
    public decimal TotalCost => Results.Sum(r => r.EstimatedCost);
    public decimal Cpvc => VerifiedCount > 0 ? TotalCost / VerifiedCount : 0;
    public double FirstPassRate => TotalTasks > 0 ? (double)VerifiedCount / TotalTasks * 100 : 0;
    public double AverageFilesChanged => Results.Count > 0 ? Results.Average(r => r.FilesChanged) : 0;
}
