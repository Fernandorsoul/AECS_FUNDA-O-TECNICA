using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public sealed class PromotionApproval
{
    public PromotionApprovalKind Kind { get; init; }
    public string Reference { get; init; } = string.Empty;
    public DateTime ConfirmedAt { get; init; } = DateTime.UtcNow;
}

public sealed class CandidatePromotionRequest
{
    public Guid EvidenceId { get; init; }
    public string RepositoryPath { get; init; } = string.Empty;
    public string ExpectedDiffHash { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public PromotionApproval Approval { get; init; } = new();
}

public sealed class CandidatePatchExportRequest
{
    public Guid EvidenceId { get; init; }
    public string DestinationPath { get; init; } = string.Empty;
    public string ExpectedDiffHash { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
}

public sealed class CandidatePromotionEvidence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public Guid ExecutionEvidenceId { get; init; }
    public Guid CandidateId { get; init; }
    public CandidatePromotionAction Action { get; init; }
    public CandidatePromotionStatus Status { get; init; }
    public PromotionEligibility Eligibility { get; init; }
    public string Actor { get; init; } = string.Empty;
    public PromotionApprovalKind ApprovalKind { get; init; }
    public string ApprovalReference { get; init; } = string.Empty;
    public DateTime? ConfirmedAt { get; init; }
    public string BaselineCommit { get; init; } = string.Empty;
    public string DiffHash { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public DateTime StartedAt { get; init; } = DateTime.UtcNow;
    public DateTime FinishedAt { get; init; } = DateTime.UtcNow;
}

public sealed class CandidatePromotionResult
{
    public CandidatePromotionStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public string OutputPath { get; init; } = string.Empty;
    public CandidatePromotionEvidence Evidence { get; init; } = new();

    public bool Succeeded => Status is
        CandidatePromotionStatus.Exported or CandidatePromotionStatus.Promoted;
}
