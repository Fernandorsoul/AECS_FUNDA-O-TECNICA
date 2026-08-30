using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public enum AcceptanceEvidenceType
{
    None,
    Verifier,
    Test
}

public sealed class AcceptanceEvidenceRequirement
{
    public AcceptanceEvidenceType Type { get; init; }
    public string Reference { get; init; } = string.Empty;
    public string TestPath { get; init; } = string.Empty;
    public bool EquivalentBehavioralEvidence { get; init; }
}

public sealed class AcceptanceCriterion
{
    public string Id { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Required { get; init; } = true;
    public bool Behavioral { get; init; }
    public AcceptanceEvidenceRequirement Evidence { get; init; } = new();
}

public sealed class AcceptanceCriterionResult
{
    public string CriterionId { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public bool Required { get; init; }
    public bool Behavioral { get; init; }
    public AcceptanceEvidenceType EvidenceType { get; init; }
    public string EvidenceReference { get; init; } = string.Empty;
    public string TestPath { get; init; } = string.Empty;
    public VerificationStatus Status { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<string> EvidenceReferences { get; init; } = [];
}
