using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public enum EvidenceGraphNodeKind
{
    Repository,
    Task,
    Execution,
    AgentRun,
    AgentAttempt,
    Baseline,
    Context,
    Candidate,
    Command,
    Verification,
    AcceptanceCriterion,
    Decision,
    Replay,
    Promotion
}

public enum EvidenceGraphDataStatus
{
    Valid,
    Missing,
    Invalid
}

public sealed class EvidenceReadScope
{
    public string RepositoryPath { get; init; } = string.Empty;
    public string Principal { get; init; } = string.Empty;
}

public sealed class EvidenceGraphQuery
{
    public string? TaskId { get; init; }
    public Guid? RunId { get; init; }
    public Guid? CandidateId { get; init; }
    public string? BaselineCommit { get; init; }
    public TaskDecision? Decision { get; init; }
    public Guid? PromotionId { get; init; }
    public int Limit { get; init; } = 100;
}

public sealed class EvidenceGraphSummary
{
    public Guid EvidenceId { get; init; }
    public string RepositoryId { get; init; } = string.Empty;
    public string TaskId { get; init; } = string.Empty;
    public Guid RunId { get; init; }
    public Guid CandidateId { get; init; }
    public string BaselineCommit { get; init; } = string.Empty;
    public TaskDecision Decision { get; init; }
    public string DiffHash { get; init; } = string.Empty;
    public string EvidenceHash { get; init; } = string.Empty;
    public List<Guid> PromotionIds { get; init; } = [];
    public List<Guid> ReplayIds { get; init; } = [];
    public DateTime CreatedAt { get; init; }
    public DateTime UpdatedAt { get; init; }
}

public sealed class EvidenceGraphNode
{
    public string Id { get; init; } = string.Empty;
    public EvidenceGraphNodeKind Kind { get; init; }
    public string Label { get; init; } = string.Empty;
    public string Origin { get; init; } = string.Empty;
    public string Authority { get; init; } = string.Empty;
    public DateTime? Timestamp { get; init; }
    public EvidenceGraphDataStatus Status { get; init; } = EvidenceGraphDataStatus.Valid;
    public Dictionary<string, string> Hashes { get; init; } = [];
    public Dictionary<string, string> Attributes { get; init; } = [];
}

public sealed class EvidenceGraphEdge
{
    public string Id { get; init; } = string.Empty;
    public string From { get; init; } = string.Empty;
    public string To { get; init; } = string.Empty;
    public string Kind { get; init; } = string.Empty;
    public string Origin { get; init; } = string.Empty;
    public string Authority { get; init; } = string.Empty;
    public DateTime? Timestamp { get; init; }
}

public sealed class EvidenceGraph
{
    public const string CurrentSchemaVersion = "aecs.evidence-graph/v1";

    public string SchemaVersion { get; init; } = CurrentSchemaVersion;
    public string Id { get; init; } = string.Empty;
    public Guid EvidenceId { get; init; }
    public string RepositoryId { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string Principal { get; init; } = string.Empty;
    public EvidenceGraphSummary Summary { get; init; } = new();
    public List<EvidenceGraphNode> Nodes { get; init; } = [];
    public List<EvidenceGraphEdge> Edges { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
}

public sealed class EvidenceGraphQueryResult
{
    public string SchemaVersion { get; init; } = EvidenceGraph.CurrentSchemaVersion;
    public List<EvidenceGraphSummary> Items { get; init; } = [];
    public List<string> Diagnostics { get; init; } = [];
}
