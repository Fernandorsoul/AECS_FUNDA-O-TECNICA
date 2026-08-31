using AECS.Domain.Models;

namespace AECS.Infrastructure.Repositories;

internal sealed class SignedExecutionEvidenceEnvelope
{
    public string SchemaVersion { get; init; } = string.Empty;
    public ExecutionEvidence Evidence { get; init; } = new();
    public EvidenceSeal Seal { get; init; } = new();
    public List<SignedPromotionEvent> PromotionEvents { get; init; } = [];
    public List<SignedReplayEvent> ReplayEvents { get; init; } = [];
    public EvidenceSeal ChainSeal { get; set; } = new();
}

internal sealed class EvidenceSeal
{
    public string Algorithm { get; init; } = string.Empty;
    public string KeyId { get; init; } = string.Empty;
    public string PayloadSha256 { get; init; } = string.Empty;
    public string Signature { get; init; } = string.Empty;
    public DateTime SignedAt { get; init; }
}

internal sealed class SignedPromotionEvent
{
    public int Sequence { get; init; }
    public string PreviousSignature { get; init; } = string.Empty;
    public CandidatePromotionEvidence Promotion { get; init; } = new();
    public EvidenceSeal Seal { get; init; } = new();
}

internal sealed class SignedReplayEvent
{
    public int Sequence { get; init; }
    public string PreviousSignature { get; init; } = string.Empty;
    public ExecutionReplayEvidence Replay { get; init; } = new();
    public EvidenceSeal Seal { get; init; } = new();
}
