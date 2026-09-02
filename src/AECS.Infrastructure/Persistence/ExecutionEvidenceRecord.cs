namespace AECS.Infrastructure.Persistence;

public sealed class ExecutionEvidenceRecord
{
    public Guid Id { get; set; }
    public string TaskId { get; set; } = string.Empty;
    public Guid AgentRunId { get; set; }
    public Guid CandidateId { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string EvidenceContentHash { get; set; } = string.Empty;
    public string EvidenceJson { get; set; } = "{}";
    public string EvidenceSealJson { get; set; } = "{}";
    public string ChainSealJson { get; set; } = "{}";
    public int EventCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<PromotionEvidenceRecord> PromotionEvents { get; set; } = [];
    public List<ReplayEvidenceRecord> ReplayEvents { get; set; } = [];
}

public sealed class PromotionEvidenceRecord
{
    public Guid Id { get; set; }
    public Guid ExecutionEvidenceId { get; set; }
    public int Sequence { get; set; }
    public string PreviousSignature { get; set; } = string.Empty;
    public string PromotionJson { get; set; } = "{}";
    public string SealJson { get; set; } = "{}";
    public DateTime SignedAt { get; set; }
    public ExecutionEvidenceRecord ExecutionEvidence { get; set; } = null!;
}

public sealed class ReplayEvidenceRecord
{
    public Guid Id { get; set; }
    public Guid ExecutionEvidenceId { get; set; }
    public int Sequence { get; set; }
    public string PreviousSignature { get; set; } = string.Empty;
    public string ReplayJson { get; set; } = "{}";
    public string SealJson { get; set; } = "{}";
    public DateTime SignedAt { get; set; }
    public ExecutionEvidenceRecord ExecutionEvidence { get; set; } = null!;
}

public sealed class HistoricalDecisionStorageRecord
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string Source { get; set; } = string.Empty;
    public string Authority { get; set; } = string.Empty;
    public int ReviewStatus { get; set; }
    public int Enforcement { get; set; }
    public DateTime ValidFrom { get; set; }
    public DateTime? ValidUntil { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string DecisionJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}

public sealed class HistoricalDecisionSuppressionStorageRecord
{
    public string Id { get; set; } = string.Empty;
    public int Version { get; set; }
    public string SchemaVersion { get; set; } = string.Empty;
    public string DecisionId { get; set; } = string.Empty;
    public int DecisionVersion { get; set; }
    public string Actor { get; set; } = string.Empty;
    public DateTime ExpiresAt { get; set; }
    public string ContentHash { get; set; } = string.Empty;
    public string SuppressionJson { get; set; } = "{}";
    public DateTime CreatedAt { get; set; }
}
