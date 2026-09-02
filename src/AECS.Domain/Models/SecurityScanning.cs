using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;

namespace AECS.Domain.Models;

public static class SecurityScanSchema
{
    public const string PolicyVersion = "aecs.security-scan/v1";
    public const string EvidenceVersion = "aecs.security-scan-evidence/v1";
    public const string ScannerVersion = "aecs.security-scanner/1.0.0";
    public const string VulnerabilityDatabaseVersion = "aecs.nuget-advisories/2026-08-31";
}

public static class SecurityScannerIds
{
    public const string Secrets = "secrets";
    public const string Dependencies = "dependencies";
    public const string Patterns = "patterns";

    public static IReadOnlySet<string> All { get; } = new HashSet<string>(
        [Secrets, Dependencies, Patterns],
        StringComparer.Ordinal);
}

public sealed class SecurityScanPolicy
{
    public string Version { get; init; } = SecurityScanSchema.PolicyVersion;
    public List<string> Scanners { get; init; } =
        [SecurityScannerIds.Secrets, SecurityScannerIds.Dependencies, SecurityScannerIds.Patterns];
    public Severity BlockAtOrAbove { get; init; } = Severity.Error;
    public string VulnerabilityDatabaseVersion { get; init; } =
        SecurityScanSchema.VulnerabilityDatabaseVersion;
    public List<SecurityScanSuppression> Suppressions { get; init; } = [];
}

public sealed class SecurityScanSuppression
{
    public string Rule { get; init; } = string.Empty;
    public string Path { get; init; } = string.Empty;
    public string Fingerprint { get; init; } = string.Empty;
    public string Justification { get; init; } = string.Empty;
}

public enum SecurityFindingDisposition
{
    Baseline,
    New,
    Suppressed
}

public sealed class SecurityFinding
{
    public string Scanner { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Rule { get; init; } = string.Empty;
    public Severity Severity { get; init; }
    public string Path { get; init; } = string.Empty;
    public int Line { get; init; }
    public string Fingerprint { get; init; } = string.Empty;
    public string Message { get; init; } = string.Empty;
    public string Advisory { get; init; } = string.Empty;
    public SecurityFindingDisposition Disposition { get; init; }
    public string SuppressionJustification { get; init; } = string.Empty;
}

public sealed class SecurityScannerRunEvidence
{
    public string Scanner { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ConfigurationVersion { get; init; } = string.Empty;
    public bool Conclusive { get; init; }
    public string Message { get; init; } = string.Empty;
    public int FindingCount { get; init; }
}

public sealed class SecurityScanEvidence
{
    public string SchemaVersion { get; init; } = SecurityScanSchema.EvidenceVersion;
    public string PolicyVersion { get; init; } = string.Empty;
    public string PolicyHash { get; init; } = string.Empty;
    public string ScannerVersion { get; init; } = SecurityScanSchema.ScannerVersion;
    public string VulnerabilityDatabaseVersion { get; init; } = string.Empty;
    public bool IsBaseline { get; init; }
    public Severity BlockAtOrAbove { get; init; }
    public List<SecurityScannerRunEvidence> Scanners { get; init; } = [];
    public List<SecurityFinding> Findings { get; init; } = [];
}

public sealed class SecurityScannerContext
{
    public VerificationContext Verification { get; init; } = new();
    public IProcessRunner ProcessRunner { get; init; } = null!;
    public SecurityScanPolicy Policy { get; init; } = new();
    public bool IsBaseline { get; init; }
    public TimeSpan Timeout { get; init; }
}

public sealed class SecurityScannerResult
{
    public string Scanner { get; init; } = string.Empty;
    public string Category { get; init; } = string.Empty;
    public string Version { get; init; } = string.Empty;
    public string ConfigurationVersion { get; init; } = string.Empty;
    public bool Conclusive { get; init; }
    public string Message { get; init; } = string.Empty;
    public List<SecurityFinding> Findings { get; init; } = [];
}

public static class SecurityScanPolicyFingerprint
{
    public static string Create(SecurityScanPolicy policy) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(policy)))).ToLowerInvariant();
}

public static class SecurityFindingFingerprint
{
    public static string Create(string rule, string path, string identity) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            string.Join('\n', rule, path.Replace('\\', '/'), identity))))
            .ToLowerInvariant();
}
