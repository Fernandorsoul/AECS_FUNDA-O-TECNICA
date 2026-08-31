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
    public int PromotionCount { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime UpdatedAt { get; set; }
    public List<PromotionEvidenceRecord> PromotionEvents { get; set; } = [];
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
