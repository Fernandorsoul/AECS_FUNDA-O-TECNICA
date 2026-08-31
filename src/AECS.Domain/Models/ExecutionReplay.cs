namespace AECS.Domain.Models;

public enum ExecutionReplayOutcome
{
    Reproduced,
    BaselineUnavailable,
    EnvironmentDivergence,
    CandidateDivergence,
    GateNotReproducible,
    Failed
}

public enum ReplayComparisonStatus
{
    Match,
    Diverged,
    Missing,
    NotReproducible
}

public sealed class ReplayCommandComparison
{
    public string Phase { get; init; } = string.Empty;
    public string FileName { get; init; } = string.Empty;
    public List<string> Arguments { get; init; } = [];
    public ReplayComparisonStatus Definition { get; init; }
    public ReplayComparisonStatus Result { get; init; }
    public int? ExpectedExitCode { get; init; }
    public int? ActualExitCode { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class ReplayGateComparison
{
    public string Gate { get; init; } = string.Empty;
    public ReplayComparisonStatus Status { get; init; }
    public string Expected { get; init; } = string.Empty;
    public string Actual { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
}

public sealed class ReplayToolComparison
{
    public string Tool { get; init; } = string.Empty;
    public ReplayComparisonStatus Status { get; init; }
    public string ExpectedVersion { get; init; } = string.Empty;
    public string ActualVersion { get; init; } = string.Empty;
}

public sealed class ExecutionReplayEvidence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ExecutionEvidenceId { get; init; }
    public Guid CandidateId { get; init; }
    public ExecutionReplayOutcome Outcome { get; init; }
    public string RepositoryPath { get; init; } = string.Empty;
    public string RequestedRepositoryPath { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public string ExpectedDiffHash { get; init; } = string.Empty;
    public string ActualDiffHash { get; init; } = string.Empty;
    public List<ReplayToolComparison> Tools { get; init; } = [];
    public List<ReplayCommandComparison> Commands { get; init; } = [];
    public List<ReplayGateComparison> Gates { get; init; } = [];
    public List<AcceptanceCriterionResult> AcceptanceCriteria { get; init; } = [];
    public string Message { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime FinishedAt { get; init; } = DateTime.UtcNow;
}

public sealed class ExecutionReplayRequest
{
    public Guid EvidenceId { get; init; }
    public string RepositoryPath { get; init; } = string.Empty;
}

public sealed class ExecutionReplayResult
{
    public ExecutionReplayOutcome Outcome { get; init; }
    public string Message { get; init; } = string.Empty;
    public ExecutionReplayEvidence Evidence { get; init; } = new();

    public bool Succeeded => Outcome == ExecutionReplayOutcome.Reproduced;
}
