using AECS.Domain.Enums;
using System.Text.Json.Serialization;

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

public sealed class CandidateReviewRequest
{
    public Guid EvidenceId { get; init; }
    public string RepositoryPath { get; init; } = string.Empty;
    public string ExpectedDiffHash { get; init; } = string.Empty;
    public string Actor { get; init; } = string.Empty;
    public CandidateReviewDecision Decision { get; init; }
    public string Justification { get; init; } = string.Empty;
    public DateTime ValidUntil { get; init; }
    public string PolicyReference { get; init; } = string.Empty;
    public Guid? RelatedReviewId { get; init; }
}

public sealed class CandidateReviewGate
{
    public string Phase { get; init; } = string.Empty;
    public string Verifier { get; init; } = string.Empty;
    public VerificationStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
}

public sealed class CandidateReviewSnapshot
{
    public bool Available { get; init; }
    public string Message { get; init; } = string.Empty;
    public Guid EvidenceId { get; init; }
    public string TaskId { get; init; } = string.Empty;
    public Guid CandidateId { get; init; }
    public string RepositoryPath { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public string BaselineBranch { get; init; } = string.Empty;
    public string Diff { get; init; } = string.Empty;
    public string DiffHash { get; init; } = string.Empty;
    public List<string> ChangedFiles { get; init; } = [];
    public RiskLevel Risk { get; init; }
    public TaskDecision Decision { get; init; }
    public TaskState State { get; init; }
    public PromotionEligibility Eligibility { get; init; }
    public bool Reviewable { get; init; }
    public bool RepositoryReady { get; init; }
    public string RepositoryState { get; init; } = string.Empty;
    public List<CandidateReviewGate> Gates { get; init; } = [];
}

public sealed class CandidateReviewResult
{
    public CandidatePromotionStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public CandidatePromotionEvidence Evidence { get; init; } = new();
    public bool Persisted { get; init; }
}

public static class CandidateReviewReference
{
    public const string Prefix = "aecs-review/";

    public static string Create(Guid reviewId) => Prefix + reviewId.ToString("N");

    public static bool TryParse(string? reference, out Guid reviewId)
    {
        reviewId = Guid.Empty;
        return reference is not null &&
            reference.StartsWith(Prefix, StringComparison.Ordinal) &&
            Guid.TryParseExact(reference[Prefix.Length..], "N", out reviewId);
    }
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
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public CandidateReviewDecision? ReviewDecision { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Justification { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public DateTime? ValidUntil { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? PolicyReference { get; init; }
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public Guid? RelatedReviewId { get; init; }
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
