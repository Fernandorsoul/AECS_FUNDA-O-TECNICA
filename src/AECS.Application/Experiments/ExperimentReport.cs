using AECS.Application.ContextCompiler;
using AECS.Domain.Enums;
using AECS.Domain.Models;
using System.Text.Json.Serialization;

namespace AECS.Application.Experiments;

public sealed class ExperimentContextFile
{
    public string Path { get; init; } = string.Empty;
    public string IncludedSha256 { get; init; } = string.Empty;
    public int Rank { get; init; }
}

public class TaskExperimentResult
{
    public string RunKey { get; init; } = string.Empty;
    public ExperimentResultStatus Status { get; init; }
    public string DatasetId { get; init; } = string.Empty;
    public string DatasetVersion { get; init; } = string.Empty;
    public string DatasetHash { get; init; } = string.Empty;
    public string VariantId { get; init; } = string.Empty;
    public int Repetition { get; init; }
    public string Provider { get; init; } = string.Empty;
    public string Adapter { get; init; } = string.Empty;
    public string RequestedModel { get; init; } = string.Empty;
    public string ContextStrategy { get; init; } = string.Empty;
    public ContextCompilationOptions ContextConfiguration { get; init; } = new();
    public string ActualContextStrategy { get; init; } = string.Empty;
    public string ContextStrategyVersion { get; init; } = string.Empty;
    public string ContextManifestHash { get; init; } = string.Empty;
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HarnessManifestHash { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HarnessVariantId { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? HarnessSecurityBaseline { get; init; }
    public List<ExperimentContextFile> IncludedContext { get; init; } = [];
    public int? Seed { get; init; }
    public Dictionary<string, string> Parameters { get; init; } =
        new(StringComparer.Ordinal);
    public string BaselineCommit { get; init; } = string.Empty;
    public TaskDecision? ExpectedDecision { get; init; }
    public bool MatchesExpected { get; init; } = true;
    public string Failure { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public RiskLevel Risk { get; init; }
    public string Model { get; init; } = string.Empty;
    public TaskDecision? Decision { get; init; }
    public string DecisionReason { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public decimal EstimatedCost { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public AgentUsageAccounting? UsageAccounting { get; init; }
    public int FilesChanged { get; init; }
    public Dictionary<string, VerificationStatus> Verifications { get; init; } = new();
    public List<AcceptanceCriterionResult> AcceptanceCriteria { get; init; } = [];
    public List<AgentAttemptEvidence> AgentAttempts { get; init; } = [];
    public ExecutionBudgetEvidence BudgetUsage { get; init; } = new();
    public int RetryCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool VerifiedCodeChange { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public bool FirstPassVerified { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ScopeViolationCount { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingDefault)]
    public int ReworkCount { get; init; }
    [JsonIgnore]
    public int TotalTokens => InputTokens + OutputTokens;
    public Guid EvidenceId { get; init; }
    public string EvidenceLocation { get; init; } = string.Empty;
    public bool OriginalRepositoryUnchanged { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? FinishedAtUtc { get; init; }
}

public class ExperimentReport
{
    public string SchemaVersion { get; init; } = ExperimentDatasetSchema.ReportVersion;
    public Guid ExperimentId { get; init; } = Guid.NewGuid();
    public DateTime ExecutedAt { get; init; } = DateTime.UtcNow;
    public string DatasetId { get; init; } = string.Empty;
    public string DatasetVersion { get; init; } = string.Empty;
    public string DatasetHash { get; init; } = string.Empty;
    public string ManifestPath { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public string ReferenceVariantId { get; init; } = string.Empty;
    public int Repetitions { get; init; } = 1;
    public ExperimentEnvironment Environment { get; init; } =
        ExperimentEnvironment.Capture();
    public List<TaskExperimentResult> Results { get; init; } = [];
    public List<ExperimentPairedComparison> PairedComparisons { get; init; } = [];
    public List<ExperimentCostRecord> CostRecords { get; init; } = [];
    public ExperimentCostEfficiencyAnalysis CostEfficiency { get; init; } = new();
    public ExperimentCostReconciliationMetadata CostReconciliation { get; init; } = new();
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExperimentHypothesisAnalysis? Analysis { get; init; }

    public int TotalTasks => Results.Count;
    public int CompletedCount => Results.Count(r => r.Status == ExperimentResultStatus.Completed);
    public int FailedCount => Results.Count(r => r.Status == ExperimentResultStatus.Failed);
    public int SkippedCount => Results.Count(r => r.Status == ExperimentResultStatus.Skipped);
    public int VerifiedCount => Results.Count(VerifiedCodeChangePolicy.IsVerified);
    public int RejectedCount => Results.Count(r =>
        r.Status == ExperimentResultStatus.Completed &&
        r.Decision == TaskDecision.Rejected);
    public int HumanReviewCount => Results.Count(r =>
        r.Status == ExperimentResultStatus.Completed &&
        r.Decision == TaskDecision.HumanReviewRequired);
    public TimeSpan TotalDuration => Results.Aggregate(TimeSpan.Zero, (acc, r) => acc + r.Duration);
    public decimal TotalEstimatedCost => Results.Sum(r => r.EstimatedCost);
    public decimal? TotalCost => OverallCostEfficiency?.TotalEffectiveCostUsd;
    public decimal? Cpvc => OverallCostEfficiency?.CpvcUsd;
    public int FirstPassVerifiedCount => Results.Count(result =>
        VerifiedCodeChangePolicy.IsVerified(result) && result.RetryCount == 0);
    public double FirstPassRate => CompletedCount > 0
        ? (double)FirstPassVerifiedCount / CompletedCount * 100
        : 0;
    public double AverageFilesChanged => CompletedCount > 0
        ? Results.Where(result => result.Status == ExperimentResultStatus.Completed)
            .Average(result => result.FilesChanged)
        : 0;
    public bool Succeeded => Results.Count > 0 &&
        Results.All(result => result.Status == ExperimentResultStatus.Completed &&
            result.MatchesExpected);

    [JsonIgnore]
    private ExperimentCostEfficiencyAggregate? OverallCostEfficiency =>
        CostEfficiency.Aggregates.SingleOrDefault(aggregate =>
            aggregate.Dimension == "overall" && aggregate.Value == "all");
}
