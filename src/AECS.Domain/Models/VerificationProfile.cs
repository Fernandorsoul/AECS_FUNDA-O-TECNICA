using System.Text.Json.Serialization;

namespace AECS.Domain.Models;

public class VerificationProfile
{
    public bool Build { get; init; } = true;
    public bool UnitTests { get; init; } = true;
    public bool IntegrationTests { get; init; }
    public bool Scope { get; init; } = true;
    public bool Budget { get; init; } = true;
    public bool SecurityScan { get; init; }
    public bool Architecture { get; init; }
    public bool BlockCriticalSemanticFailures { get; init; } = true;
    public List<string> RequiredSemanticVerifiers { get; init; } = [];

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SecurityScanPolicy? SecurityPolicy { get; init; }

    [JsonIgnore]
    public SecurityScanPolicy EffectiveSecurityPolicy => SecurityPolicy ?? new();
}
