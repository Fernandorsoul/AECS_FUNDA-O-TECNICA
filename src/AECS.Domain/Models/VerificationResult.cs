using AECS.Domain.Enums;

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
}
