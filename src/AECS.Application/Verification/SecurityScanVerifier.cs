using System.Text.Json;
using System.Text.RegularExpressions;
using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public sealed class SecurityScanVerifier : IVerifier
{
    public const string VerifierName = "SecurityScan";
    private readonly IProcessRunner _processRunner;
    private readonly IReadOnlyDictionary<string, ISecurityScanner> _scanners;
    private readonly IReadOnlySet<string> _baselineFingerprints;
    private readonly bool _isBaseline;
    private readonly Func<TimeSpan>? _remainingDuration;

    public SecurityScanVerifier(
        IProcessRunner processRunner,
        IEnumerable<ISecurityScanner>? scanners = null,
        IReadOnlySet<string>? baselineFingerprints = null,
        bool isBaseline = false,
        Func<TimeSpan>? remainingDuration = null)
    {
        _processRunner = processRunner;
        _scanners = (scanners ?? CreateDefaultScanners())
            .ToDictionary(scanner => scanner.Id, StringComparer.Ordinal);
        _baselineFingerprints = baselineFingerprints ?? new HashSet<string>(StringComparer.Ordinal);
        _isBaseline = isBaseline;
        _remainingDuration = remainingDuration;
    }

    public string Name => VerifierName;
    public VerificationCategory Category => VerificationCategory.HighConfidence;

    public static IReadOnlyList<ISecurityScanner> CreateDefaultScanners() =>
        [new SecretSecurityScanner(), new DotNetDependencySecurityScanner(), new PatternSecurityScanner()];

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        var policy = context.Contract.Verification.EffectiveSecurityPolicy;
        var policyHash = SecurityScanPolicyFingerprint.Create(policy);
        var results = new List<SecurityScannerResult>();
        foreach (var scannerId in policy.Scanners)
        {
            if (!_scanners.TryGetValue(scannerId, out var scanner))
            {
                results.Add(new SecurityScannerResult
                {
                    Scanner = scannerId,
                    Category = scannerId,
                    Version = "unavailable",
                    ConfigurationVersion = policy.Version,
                    Conclusive = false,
                    Message = "Configured scanner is unavailable."
                });
                continue;
            }

            var timeout = Remaining(context.Contract.Budget);
            if (timeout <= TimeSpan.Zero)
            {
                results.Add(new SecurityScannerResult
                {
                    Scanner = scannerId,
                    Category = scannerId,
                    Version = "unavailable",
                    ConfigurationVersion = policy.Version,
                    Conclusive = false,
                    Message = "Wall-clock budget was exhausted before the scanner ran."
                });
                continue;
            }

            try
            {
                results.Add(await scanner.ScanAsync(new SecurityScannerContext
                {
                    Verification = context,
                    ProcessRunner = _processRunner,
                    Policy = policy,
                    IsBaseline = _isBaseline,
                    Timeout = timeout
                }, cancellationToken));
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                results.Add(new SecurityScannerResult
                {
                    Scanner = scannerId,
                    Category = scannerId,
                    Version = "failed",
                    ConfigurationVersion = policy.Version,
                    Conclusive = false,
                    Message = $"Scanner failed closed ({ex.GetType().Name}); diagnostic text was omitted."
                });
            }
        }

        var findings = results.SelectMany(result => result.Findings)
            .Select(finding => ApplyDisposition(finding, policy))
            .OrderBy(finding => finding.Path, StringComparer.Ordinal)
            .ThenBy(finding => finding.Line)
            .ThenBy(finding => finding.Rule, StringComparer.Ordinal)
            .ToList();
        var evidence = new SecurityScanEvidence
        {
            PolicyVersion = policy.Version,
            PolicyHash = policyHash,
            VulnerabilityDatabaseVersion = policy.VulnerabilityDatabaseVersion,
            IsBaseline = _isBaseline,
            BlockAtOrAbove = policy.BlockAtOrAbove,
            Scanners = results.Select(result => new SecurityScannerRunEvidence
            {
                Scanner = result.Scanner,
                Category = result.Category,
                Version = result.Version,
                ConfigurationVersion = result.ConfigurationVersion,
                Conclusive = result.Conclusive,
                Message = result.Message,
                FindingCount = result.Findings.Count
            }).ToList(),
            Findings = findings
        };

        var inconclusive = results.Where(result => !result.Conclusive).ToList();
        if (inconclusive.Count > 0)
        {
            return Result(
                context.AgentRunId,
                VerificationStatus.Error,
                Severity.Critical,
                $"Security scan was inconclusive: {string.Join("; ", inconclusive.Select(result => $"{result.Scanner}: {result.Message}"))}",
                evidence);
        }

        var blocking = findings.Where(finding =>
            finding.Disposition == SecurityFindingDisposition.New &&
            finding.Severity >= policy.BlockAtOrAbove).ToList();
        if (blocking.Count > 0)
        {
            return Result(
                context.AgentRunId,
                VerificationStatus.Fail,
                blocking.Max(finding => finding.Severity),
                $"Security scan found {blocking.Count} new blocking finding(s); sensitive values are omitted.",
                evidence);
        }

        var baselineDebt = findings.Count(finding =>
            finding.Disposition == SecurityFindingDisposition.Baseline);
        var suppressed = findings.Count(finding =>
            finding.Disposition == SecurityFindingDisposition.Suppressed);
        return Result(
            context.AgentRunId,
            VerificationStatus.Pass,
            findings.Count == 0 ? Severity.Info : Severity.Warning,
            _isBaseline
                ? $"Security baseline inventory completed with {findings.Count} finding(s)."
                : $"Security scan passed with no new blocking findings ({baselineDebt} baseline, {suppressed} suppressed).",
            evidence);
    }

    private SecurityFinding ApplyDisposition(SecurityFinding finding, SecurityScanPolicy policy)
    {
        var suppression = policy.Suppressions.FirstOrDefault(item =>
            item.Rule.Equals(finding.Rule, StringComparison.Ordinal) &&
            item.Path.Replace('\\', '/').Equals(finding.Path, StringComparison.Ordinal) &&
            (string.IsNullOrEmpty(item.Fingerprint) ||
             item.Fingerprint.Equals(finding.Fingerprint, StringComparison.Ordinal)));
        var disposition = suppression is not null
            ? SecurityFindingDisposition.Suppressed
            : _isBaseline || _baselineFingerprints.Contains(finding.Fingerprint)
                ? SecurityFindingDisposition.Baseline
                : SecurityFindingDisposition.New;
        return new SecurityFinding
        {
            Scanner = finding.Scanner,
            Category = finding.Category,
            Rule = finding.Rule,
            Severity = finding.Severity,
            Path = finding.Path,
            Line = finding.Line,
            Fingerprint = finding.Fingerprint,
            Message = finding.Message,
            Advisory = finding.Advisory,
            Disposition = disposition,
            SuppressionJustification = suppression?.Justification ?? string.Empty
        };
    }

    private TimeSpan Remaining(ExecutionBudget budget)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(1, budget.MaxDurationSeconds));
        if (_remainingDuration is null)
            return configured;
        var remaining = _remainingDuration();
        return remaining < configured ? remaining : configured;
    }

    private static VerificationResult Result(
        string agentRunId,
        VerificationStatus status,
        Severity severity,
        string message,
        SecurityScanEvidence evidence) => new()
        {
            AgentRunId = agentRunId,
            Verifier = VerifierName,
            Status = status,
            Severity = severity,
            Message = message,
            SecurityScan = evidence
        };

}

internal sealed class SecretSecurityScanner : ISecurityScanner
{
    private static readonly (string Rule, Severity Severity, Regex Pattern)[] Rules =
    [
        ("AECS-SECRET-AWS-ACCESS-KEY", Severity.Critical,
            Pattern(@"\bAKIA[0-9A-Z]{16}\b")),
        ("AECS-SECRET-GITHUB-TOKEN", Severity.Critical,
            Pattern(@"\bgh[pousr]_[A-Za-z0-9]{36,255}\b")),
        ("AECS-SECRET-PRIVATE-KEY", Severity.Critical,
            Pattern(@"-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----")),
        ("AECS-SECRET-CREDENTIAL-ASSIGNMENT", Severity.Error,
            Pattern("(?i)\\b(?:password|passwd|pwd|secret|api[_-]?key|token)\\b\\s*[:=]\\s*[\"']([^\"'\\r\\n]{8,})[\"']"))
    ];

    public string Id => SecurityScannerIds.Secrets;

    public Task<SecurityScannerResult> ScanAsync(
        SecurityScannerContext context,
        CancellationToken cancellationToken)
    {
        var findings = new List<SecurityFinding>();
        foreach (var file in SecurityScanFileCatalog.Read(context.Verification.RepoPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (rule, severity, pattern) in Rules)
            {
                foreach (Match match in pattern.Matches(file.Content))
                {
                    var line = SecurityScanFileCatalog.LineNumber(file.Content, match.Index);
                    var lineIdentity = SecurityScanFileCatalog.LineIdentity(file.Content, match.Index);
                    findings.Add(new SecurityFinding
                    {
                        Scanner = Id,
                        Category = "secret",
                        Rule = rule,
                        Severity = severity,
                        Path = file.Path,
                        Line = line,
                        Fingerprint = SecurityFindingFingerprint.Create(rule, file.Path, lineIdentity),
                        Message = "Potential credential material detected; matched value was omitted."
                    });
                }
            }
        }

        return Task.FromResult(new SecurityScannerResult
        {
            Scanner = Id,
            Category = "secret",
            Version = "aecs-secret-rules/1.0.0",
            ConfigurationVersion = "aecs-secret-patterns/2026-08-31",
            Conclusive = true,
            Message = "Deterministic secret patterns completed.",
            Findings = findings
        });
    }

    private static Regex Pattern(string pattern) => new(
        pattern,
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
}

internal sealed class PatternSecurityScanner : ISecurityScanner
{
    private static readonly (string Rule, Severity Severity, string Message, Regex Pattern)[] Rules =
    [
        ("AECS-PATTERN-BINARY-FORMATTER", Severity.Error,
            "Unsafe BinaryFormatter usage detected.", Pattern(@"\bBinaryFormatter\b")),
        ("AECS-PATTERN-TLS-BYPASS", Severity.Critical,
            "TLS certificate validation bypass detected.",
            Pattern(@"ServerCertificateCustomValidationCallback\s*=\s*(?:\([^)]*\)|[^=;]+)\s*=>\s*true")),
        ("AECS-PATTERN-SHELL-PROCESS", Severity.Warning,
            "Shell process execution requires manual security review.",
            Pattern("Process\\.Start\\s*\\(\\s*[\"'](?:/bin/(?:ba)?sh|cmd(?:\\.exe)?|powershell(?:\\.exe)?)[\"']")),
        ("AECS-PATTERN-WEAK-HASH", Severity.Warning,
            "Weak cryptographic hash usage detected.", Pattern(@"\b(?:MD5|SHA1)\.Create\s*\("))
    ];

    public string Id => SecurityScannerIds.Patterns;

    public Task<SecurityScannerResult> ScanAsync(
        SecurityScannerContext context,
        CancellationToken cancellationToken)
    {
        var findings = new List<SecurityFinding>();
        foreach (var file in SecurityScanFileCatalog.Read(context.Verification.RepoPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var (rule, severity, message, pattern) in Rules)
            {
                foreach (Match match in pattern.Matches(file.Content))
                {
                    findings.Add(new SecurityFinding
                    {
                        Scanner = Id,
                        Category = "security-pattern",
                        Rule = rule,
                        Severity = severity,
                        Path = file.Path,
                        Line = SecurityScanFileCatalog.LineNumber(file.Content, match.Index),
                        Fingerprint = SecurityFindingFingerprint.Create(
                            rule,
                            file.Path,
                            SecurityScanFileCatalog.LineIdentity(file.Content, match.Index)),
                        Message = message
                    });
                }
            }
        }

        return Task.FromResult(new SecurityScannerResult
        {
            Scanner = Id,
            Category = "security-pattern",
            Version = "aecs-security-patterns/1.0.0",
            ConfigurationVersion = "aecs-csharp-security-patterns/2026-08-31",
            Conclusive = true,
            Message = "Deterministic security patterns completed.",
            Findings = findings
        });
    }

    private static Regex Pattern(string pattern) => new(
        pattern,
        RegexOptions.CultureInvariant | RegexOptions.Compiled,
        TimeSpan.FromSeconds(1));
}

internal sealed class DotNetDependencySecurityScanner : ISecurityScanner
{
    public string Id => SecurityScannerIds.Dependencies;

    public async Task<SecurityScannerResult> ScanAsync(
        SecurityScannerContext context,
        CancellationToken cancellationToken)
    {
        ResolvedRepositoryExecutionProfile execution;
        try
        {
            execution = RepositoryExecutionProfileResolver.Resolve(
                context.Verification.RepoPath,
                context.Verification.Contract.Execution);
        }
        catch (Exception ex)
        {
            return Failed($"Dependency inventory profile is invalid: {ex.Message}");
        }

        var arguments = new List<string> { "list", execution.TargetArgument!, "package" };
        arguments.AddRange(["--include-transitive", "--format", "json"]);
        var request = new ProcessExecutionRequest
        {
            FileName = "dotnet",
            Arguments = arguments,
            WorkingDirectory = execution.WorkingDirectory,
            Timeout = context.Timeout,
            Phase = context.IsBaseline
                ? ExecutionCapabilityPhases.BaselineSecurityScan
                : ExecutionCapabilityPhases.CandidateSecurityScan
        };
        var raw = await context.ProcessRunner.RunAsync(request, cancellationToken);
        var sanitized = SecurityScanOutputSanitizer.Sanitize(
            raw,
            context.Verification.RepoPath);
        context.Verification.CommandEvidence.Add(
            ExecutionCommandEvidenceFactory.Create(context.Verification, request, sanitized));
        if (!raw.Succeeded)
        {
            return Failed(raw.TimedOut
                ? "Dependency inventory timed out."
                : raw.Cancelled
                    ? "Dependency inventory was cancelled."
                    : $"Dependency inventory exited with code {raw.ExitCode}.");
        }

        List<(string Id, string Version)> packages;
        try
        {
            packages = ParsePackages(raw.StandardOutput);
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            return Failed($"Dependency inventory output was invalid: {ex.Message}");
        }

        var findings = NuGetVulnerabilityDatabase.Find(packages, execution.TargetArgument!);
        return new SecurityScannerResult
        {
            Scanner = Id,
            Category = "vulnerable-dependency",
            Version = "dotnet-list-package/json-v1",
            ConfigurationVersion = context.Policy.VulnerabilityDatabaseVersion,
            Conclusive = true,
            Message = $"Dependency inventory completed for {packages.Count} resolved package(s).",
            Findings = findings
        };
    }

    private SecurityScannerResult Failed(string message) => new()
    {
        Scanner = Id,
        Category = "vulnerable-dependency",
        Version = "dotnet-list-package/json-v1",
        ConfigurationVersion = SecurityScanSchema.VulnerabilityDatabaseVersion,
        Conclusive = false,
        Message = message
    };

    private static List<(string Id, string Version)> ParsePackages(string json)
    {
        var documentStart = json.IndexOf('{');
        if (documentStart < 0)
            throw new JsonException("Dependency inventory did not contain a JSON document.");
        using var document = JsonDocument.Parse(json[documentStart..]);
        var packages = new List<(string Id, string Version)>();
        Visit(document.RootElement, packages);
        return packages
            .GroupBy(
                item => $"{item.Id}\u001f{item.Version}",
                StringComparer.OrdinalIgnoreCase)
            .Select(group => group.First())
            .OrderBy(item => item.Id, StringComparer.OrdinalIgnoreCase)
            .ThenBy(item => item.Version, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static void Visit(JsonElement element, List<(string Id, string Version)> packages)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            if (element.TryGetProperty("id", out var id) &&
                element.TryGetProperty("resolvedVersion", out var version) &&
                id.ValueKind == JsonValueKind.String &&
                version.ValueKind == JsonValueKind.String)
            {
                packages.Add((id.GetString()!, version.GetString()!));
            }
            foreach (var property in element.EnumerateObject())
                Visit(property.Value, packages);
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                Visit(item, packages);
        }
    }
}

internal static class NuGetVulnerabilityDatabase
{
    public static List<SecurityFinding> Find(
        IEnumerable<(string Id, string Version)> packages,
        string targetPath)
    {
        var findings = new List<SecurityFinding>();
        foreach (var package in packages)
        {
            if (package.Id.Equals("Newtonsoft.Json", StringComparison.OrdinalIgnoreCase) &&
                IsBefore(package.Version, "13.0.1"))
            {
                findings.Add(Finding(
                    package,
                    targetPath,
                    "GHSA-5crp-9r3c-p9vr",
                    Severity.Error,
                    "Resolved Newtonsoft.Json version is affected by a denial-of-service advisory."));
            }
            if (package.Id.Equals("System.Text.Encodings.Web", StringComparison.OrdinalIgnoreCase) &&
                IsAffectedEncodingVersion(package.Version))
            {
                findings.Add(Finding(
                    package,
                    targetPath,
                    "GHSA-ghhp-997w-qr28",
                    Severity.Critical,
                    "Resolved System.Text.Encodings.Web version is affected by a remote-code-execution advisory."));
            }
        }
        return findings;
    }

    private static SecurityFinding Finding(
        (string Id, string Version) package,
        string targetPath,
        string advisory,
        Severity severity,
        string message) => new()
        {
            Scanner = SecurityScannerIds.Dependencies,
            Category = "vulnerable-dependency",
            Rule = advisory,
            Severity = severity,
            Path = targetPath.Replace('\\', '/'),
            Fingerprint = SecurityFindingFingerprint.Create(
                advisory,
                targetPath,
                $"{package.Id.ToLowerInvariant()}@{package.Version}"),
            Message = $"{message} Resolved package: {package.Id} {package.Version}.",
            Advisory = advisory
        };

    private static bool IsAffectedEncodingVersion(string version) =>
        InRange(version, "4.0.0", "4.5.1") ||
        InRange(version, "4.6.0", "4.7.2") ||
        Compare(version, "5.0.0") == 0;

    private static bool InRange(string version, string minimum, string exclusiveMaximum) =>
        Compare(version, minimum) >= 0 && Compare(version, exclusiveMaximum) < 0;

    private static bool IsBefore(string version, string exclusiveMaximum) =>
        Compare(version, exclusiveMaximum) < 0;

    private static int Compare(string left, string right)
    {
        if (!Version.TryParse(left.Split('-', '+')[0], out var leftVersion) ||
            !Version.TryParse(right, out var rightVersion))
        {
            throw new InvalidOperationException($"Unsupported NuGet version '{left}'.");
        }
        return leftVersion.CompareTo(rightVersion);
    }
}

internal static partial class SecurityScanOutputSanitizer
{
    private const int MaximumEvidenceCharacters = 64 * 1024;

    public static ProcessExecutionResult Sanitize(
        ProcessExecutionResult result,
        string workspacePath) => new()
        {
            ExitCode = result.ExitCode,
            StandardOutput = Clean(result.StandardOutput, workspacePath),
            StandardError = Clean(result.StandardError, workspacePath),
            Duration = result.Duration,
            TimedOut = result.TimedOut,
            Cancelled = result.Cancelled,
            Environment = result.Environment
        };

    private static string Clean(string value, string workspacePath)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var cleaned = value.Replace(
            Path.GetFullPath(workspacePath),
            "<workspace>",
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);
        cleaned = UrlCredentialRegex().Replace(cleaned, "${scheme}<redacted>@");
        cleaned = NamedSecretRegex().Replace(cleaned, "${name}=<redacted>");
        cleaned = AwsKeyRegex().Replace(cleaned, "<redacted-aws-key>");
        cleaned = GitHubTokenRegex().Replace(cleaned, "<redacted-github-token>");
        return cleaned.Length <= MaximumEvidenceCharacters
            ? cleaned
            : cleaned[..MaximumEvidenceCharacters] + "\n[TRUNCATED]";
    }

    [GeneratedRegex(@"(?<scheme>https?://)[^\s/@:]+:[^\s/@]+@", RegexOptions.IgnoreCase)]
    private static partial Regex UrlCredentialRegex();

    [GeneratedRegex(@"(?<name>(?:password|token|secret|api[_-]?key))\s*=\s*[^\s;&]+", RegexOptions.IgnoreCase)]
    private static partial Regex NamedSecretRegex();

    [GeneratedRegex(@"\bAKIA[0-9A-Z]{16}\b")]
    private static partial Regex AwsKeyRegex();

    [GeneratedRegex(@"\bgh[pousr]_[A-Za-z0-9]{36,255}\b")]
    private static partial Regex GitHubTokenRegex();
}

internal static class SecurityScanFileCatalog
{
    private const long MaximumTextFileBytes = 4 * 1024 * 1024;
    private static readonly HashSet<string> ExcludedDirectories = new(
        [".git", "bin", "obj", ".aecs-verification", "node_modules"],
        StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> TextExtensions = new(
        [
            ".cs", ".fs", ".vb", ".json", ".yaml", ".yml", ".xml", ".config",
            ".props", ".targets", ".csproj", ".fsproj", ".vbproj", ".sln", ".slnx",
            ".env", ".txt", ".md", ".sh", ".ps1", ".js", ".ts", ".py", ".java",
            ".go", ".rs", ".php", ".rb", ".pem", ".key"
        ],
        StringComparer.OrdinalIgnoreCase);

    public static IEnumerable<SecurityScanTextFile> Read(string workspacePath)
    {
        var root = Path.GetFullPath(workspacePath);
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                if (ExcludedDirectories.Contains(Path.GetFileName(child)))
                    continue;
                EnsureNotLink(child);
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                EnsureNotLink(file);
                var fileName = Path.GetFileName(file);
                if ((!TextExtensions.Contains(Path.GetExtension(file)) &&
                     !fileName.Equals(".env", StringComparison.OrdinalIgnoreCase) &&
                     !fileName.Equals("Dockerfile", StringComparison.OrdinalIgnoreCase)) ||
                    new FileInfo(file).Length > MaximumTextFileBytes)
                {
                    continue;
                }
                yield return new SecurityScanTextFile(
                    Path.GetRelativePath(root, file).Replace('\\', '/'),
                    File.ReadAllText(file));
            }
        }
    }

    public static int LineNumber(string content, int index)
    {
        var line = 1;
        for (var position = 0; position < index; position++)
        {
            if (content[position] == '\n')
                line++;
        }
        return line;
    }

    public static string LineIdentity(string content, int index)
    {
        var start = content.LastIndexOf('\n', Math.Max(0, index - 1));
        var end = content.IndexOf('\n', index);
        start = start < 0 ? 0 : start + 1;
        end = end < 0 ? content.Length : end;
        return content[start..end].Trim();
    }

    private static void EnsureNotLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidOperationException(
                "Security scan refused a symbolic link, junction, or reparse point.");
        }
    }
}

internal sealed record SecurityScanTextFile(string Path, string Content);
