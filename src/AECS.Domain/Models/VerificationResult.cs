using AECS.Domain.Enums;
using System.Text.Json.Serialization;

namespace AECS.Domain.Models;

public class VerificationResult
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string AgentRunId { get; init; } = string.Empty;
    public string Verifier { get; init; } = string.Empty;
    public VerificationStatus Status { get; init; }
    public Severity Severity { get; init; }
    public string Message { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecurityScanEvidence? SecurityScan { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TestSuiteEvidence? TestSuite { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SemanticVerificationEvidence? Semantic { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public HistoricalDecisionVerificationEvidence? Historical { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ConstraintLedgerVerificationEvidence? ConstraintLedger { get; init; }
}
