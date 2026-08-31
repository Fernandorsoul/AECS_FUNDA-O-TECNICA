using AECS.Domain.Enums;
using AECS.Domain.Models;
using System.Globalization;
using System.Text.RegularExpressions;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace AECS.Application.Parsing;

public class TaskContractParser
{
    private static readonly IDeserializer Deserializer = new DeserializerBuilder()
        .WithNamingConvention(NullNamingConvention.Instance)
        .IgnoreUnmatchedProperties()
        .Build();

    public TaskContract Parse(string yaml)
    {
        var yamlTask = Deserializer.Deserialize<TaskYamlRoot>(yaml);
        var model = yamlTask.Task ?? yamlTask.task ?? throw new InvalidOperationException("YAML must contain a 'task' root key.");

        return MapToContract(model);
    }

    public TaskContract ParseFromFile(string filePath)
    {
        var yaml = File.ReadAllText(filePath);
        return Parse(yaml);
    }

    private static TaskContract MapToContract(TaskYamlModel model)
    {
        var scope = model.Scope ?? model.scope;
        var constraints = model.Constraints ?? model.constraints;
        var budget = model.Budget ?? model.budget;
        var execution = model.Execution ?? model.execution;
        var capabilities = model.Capabilities ?? model.capabilities ??
            execution?.Capabilities ?? execution?.capabilities;
        var verification = model.Verification ?? model.verification;
        var approval = model.Approval ?? model.approval;
        var acceptance = model.Acceptance ?? model.acceptance ?? [];
        var acceptanceEvidence = model.AcceptanceEvidence ?? model.acceptance_evidence ?? [];
        var mappedBudget = new ExecutionBudget
        {
            MaxTokens = budget?.Tokens ?? budget?.tokens ?? 60000,
            MaxCostUsd = budget?.Usd ?? budget?.usd ?? 0.20m,
            MaxRetries = budget?.Retries ?? budget?.retries ?? 1,
            MaxDurationSeconds = budget?.WallClockSeconds ?? budget?.wall_clock_seconds ?? 120,
            MaxFilesChanged = budget?.MaxFilesChanged ?? budget?.max_files_changed ?? 10
        };
        ValidateBudget(mappedBudget);
        var executionRuntime = ParseExecutionRuntime(
            execution?.Runtime ?? execution?.runtime ?? RepositoryExecutionProfile.DockerRuntime);
        var sandbox = execution?.Sandbox ?? execution?.sandbox;
        var mappedSandbox = executionRuntime == RepositoryExecutionProfile.DockerRuntime
            ? new SandboxExecutionProfile
            {
                Image = sandbox?.Image ?? sandbox?.image ?? SandboxExecutionProfile.DefaultImage,
                CpuLimit = sandbox?.CpuLimit ?? sandbox?.cpu_limit ?? "1.0",
                MemoryLimit = sandbox?.MemoryLimit ?? sandbox?.memory_limit ?? "512m",
                ProcessLimit = sandbox?.ProcessLimit ?? sandbox?.process_limit ?? 128,
                WallClockSeconds = sandbox?.WallClockSeconds ?? sandbox?.wall_clock_seconds ?? 120,
                NetworkAccess = sandbox?.NetworkAccess ?? sandbox?.network_access ?? false
            }
            : null;
        if (mappedSandbox is not null)
            ValidateSandbox(mappedSandbox);
        var mappedCapabilities = MapCapabilities(capabilities);
        if (mappedSandbox is not null)
            ValidateCapabilityResources(mappedCapabilities, mappedSandbox);
        var mappedAcceptance = MapAcceptanceCriteria(acceptance, acceptanceEvidence);
        var securityScanRequired = IsRequired(
            verification?.SecurityScan ?? verification?.security_scan ?? "optional");
        var securityPolicyYaml = verification?.SecurityPolicy ?? verification?.security_policy;
        var mappedVerification = new VerificationProfile
        {
            Build = IsRequired(verification?.Build ?? verification?.build ?? "required"),
            UnitTests = IsRequired(verification?.UnitTests ?? verification?.unit_tests ?? "required"),
            IntegrationTests = IsRequired(
                verification?.IntegrationTests ?? verification?.integration_tests ?? "optional"),
            Scope = IsRequired(verification?.Scope ?? verification?.scope ?? "required"),
            SecurityScan = securityScanRequired,
            Architecture = IsRequired(
                verification?.Architecture ?? verification?.architecture ?? "optional"),
            BlockCriticalSemanticFailures = IsRequired(
                verification?.CriticalSemanticFailures
                ?? verification?.critical_semantic_failures
                ?? "required"),
            RequiredSemanticVerifiers = verification?.RequiredSemanticVerifiers
                ?? verification?.required_semantic_verifiers
                ?? [],
            SecurityPolicy = securityScanRequired || securityPolicyYaml is not null
                ? MapSecurityScanPolicy(securityPolicyYaml)
                : null
        };
        ValidateRequiredProcessCapabilities(
            mappedCapabilities,
            mappedVerification,
            mappedAcceptance);

        return new TaskContract
        {
            Id = model.Id ?? model.id ?? string.Empty,
            Objective = model.Objective ?? model.objective ?? string.Empty,
            AcceptanceCriteria = acceptance,
            AcceptanceRequirements = mappedAcceptance,
            Scope = new ScopeDefinition
            {
                Allowed = scope?.Allowed ?? scope?.allowed ?? [],
                Forbidden = scope?.Forbidden ?? scope?.forbidden ?? []
            },
            Constraints = new TaskConstraints
            {
                SecurityRisk = ParseRiskLevel(constraints?.SecurityRisk ?? constraints?.security_risk ?? "low"),
                DatabaseMigration = constraints?.DatabaseMigration ?? constraints?.database_migration ?? false,
                ExternalDependency = constraints?.ExternalDependency ?? constraints?.external_dependency ?? false
            },
            Budget = mappedBudget,
            Execution = new RepositoryExecutionProfile
            {
                WorkingDirectory = execution?.WorkingDirectory
                    ?? execution?.working_directory
                    ?? ".",
                Target = execution?.Target ?? execution?.target ?? string.Empty,
                Runtime = executionRuntime,
                Sandbox = mappedSandbox,
                Capabilities = mappedCapabilities
            },
            Verification = mappedVerification,
            Approval = new ApprovalPolicy
            {
                Production = ParseApprovalLevel(approval?.Production ?? approval?.production ?? "none")
            },
            Status = TaskState.ContractReady,
            CreatedAt = DateTime.UtcNow
        };
    }

    private static void ValidateBudget(ExecutionBudget budget)
    {
        if (budget.MaxTokens < 0)
            throw new InvalidOperationException("budget.tokens cannot be negative.");
        if (budget.MaxCostUsd < 0)
            throw new InvalidOperationException("budget.usd cannot be negative.");
        if (budget.MaxRetries < 0)
            throw new InvalidOperationException("budget.retries cannot be negative.");
        if (budget.MaxDurationSeconds <= 0)
            throw new InvalidOperationException("budget.wall_clock_seconds must be positive.");
        if (budget.MaxFilesChanged < 0)
            throw new InvalidOperationException("budget.max_files_changed cannot be negative.");
    }

    private static string ParseExecutionRuntime(string value) =>
        value.Trim().ToLowerInvariant() switch
        {
            RepositoryExecutionProfile.DockerRuntime => RepositoryExecutionProfile.DockerRuntime,
            RepositoryExecutionProfile.HostRuntime => RepositoryExecutionProfile.HostRuntime,
            _ => throw new InvalidOperationException(
                $"Unknown execution.runtime: '{value}'. Expected 'docker' or 'host'.")
        };

    private static void ValidateSandbox(SandboxExecutionProfile sandbox)
    {
        if (!Regex.IsMatch(
                sandbox.Image,
                @"^[^\s@]+@sha256:[a-fA-F0-9]{64}$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "execution.sandbox.image must be an immutable image reference pinned by sha256 digest.");
        }

        if (!decimal.TryParse(
                sandbox.CpuLimit,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var cpuLimit) || cpuLimit <= 0)
        {
            throw new InvalidOperationException(
                "execution.sandbox.cpu_limit must be a positive invariant decimal.");
        }

        if (!Regex.IsMatch(
                sandbox.MemoryLimit,
                @"^[1-9][0-9]*(?:[kKmMgG])?$",
                RegexOptions.CultureInvariant))
        {
            throw new InvalidOperationException(
                "execution.sandbox.memory_limit must be a positive Docker memory limit.");
        }

        if (sandbox.ProcessLimit <= 0)
            throw new InvalidOperationException("execution.sandbox.process_limit must be positive.");
        if (sandbox.WallClockSeconds <= 0)
            throw new InvalidOperationException("execution.sandbox.wall_clock_seconds must be positive.");
    }

    private static ExecutionCapabilityPolicy MapCapabilities(
        CapabilitiesYamlModel? model)
    {
        if (model is null)
            return ExecutionCapabilityPolicy.RestrictiveDefault();

        var defaults = new ResourceCapabilities();
        var fileSystem = model.FileSystem ?? model.file_system;
        var network = model.Network ?? model.network;
        var resources = model.Resources ?? model.resources;
        var policy = new ExecutionCapabilityPolicy
        {
            Version = model.Version ?? model.version ?? ExecutionCapabilityPolicy.CurrentVersion,
            Authority = ExecutionCapabilityPolicy.TaskContractAuthority,
            FileSystem = new FileSystemCapabilities
            {
                Read = fileSystem?.Read ?? fileSystem?.read ?? [],
                Write = fileSystem?.Write ?? fileSystem?.write ?? []
            },
            Processes = (model.Processes ?? model.processes ?? [])
                .Select(rule => new ProcessCapabilityRule
                {
                    Executable = rule.Executable ?? rule.executable ?? string.Empty,
                    ArgumentPrefix = rule.ArgumentPrefix ?? rule.argument_prefix ?? [],
                    Phases = rule.Phases ?? rule.phases ?? []
                }).ToList(),
            Network = new NetworkCapabilities
            {
                Destinations = network?.Destinations ?? network?.destinations ?? [],
                Phases = network?.Phases ?? network?.phases ?? []
            },
            Secrets = (model.Secrets ?? model.secrets ?? [])
                .Select(secret => new SecretCapability
                {
                    Name = secret.Name ?? secret.name ?? string.Empty,
                    Phases = secret.Phases ?? secret.phases ?? []
                }).ToList(),
            Resources = new ResourceCapabilities
            {
                CpuLimit = resources?.CpuLimit ?? resources?.cpu_limit ?? defaults.CpuLimit,
                MemoryLimit = resources?.MemoryLimit ?? resources?.memory_limit ?? defaults.MemoryLimit,
                ProcessLimit = resources?.ProcessLimit ?? resources?.process_limit ?? defaults.ProcessLimit,
                WallClockSeconds = resources?.WallClockSeconds ??
                    resources?.wall_clock_seconds ?? defaults.WallClockSeconds
            }
        };
        ValidateCapabilities(policy);
        return policy;
    }

    private static void ValidateCapabilities(ExecutionCapabilityPolicy policy)
    {
        if (policy.Version != ExecutionCapabilityPolicy.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported capabilities.version: '{policy.Version}'.");
        }
        if (policy.FileSystem.Read.Count != 1 || policy.FileSystem.Read[0] != "**")
        {
            throw new InvalidOperationException(
                "capabilities.file_system.read v1 must grant only the staged workspace with '**'.");
        }
        foreach (var write in policy.FileSystem.Write)
            ValidateCapabilityWritePath(write);

        foreach (var rule in policy.Processes)
        {
            if (!Regex.IsMatch(rule.Executable, @"^[a-zA-Z0-9._+-]+$"))
            {
                throw new InvalidOperationException(
                    $"Invalid capabilities.processes executable: '{rule.Executable}'.");
            }
            if (rule.ArgumentPrefix.Count == 0 ||
                rule.ArgumentPrefix.Any(string.IsNullOrWhiteSpace))
            {
                throw new InvalidOperationException(
                    $"Process capability '{rule.Executable}' requires a non-empty argument_prefix.");
            }
            ValidatePhases(rule.Phases, $"process capability '{rule.Executable}'");
        }

        if (policy.Network.Phases.Count > 0)
            ValidatePhases(policy.Network.Phases, "network capabilities");
        foreach (var destination in policy.Network.Destinations)
        {
            if (string.IsNullOrWhiteSpace(destination) ||
                destination.Any(char.IsWhiteSpace) ||
                destination.Contains('/') && !Uri.TryCreate(destination, UriKind.Absolute, out _))
            {
                throw new InvalidOperationException(
                    $"Invalid capabilities.network destination: '{destination}'.");
            }
        }
        if (policy.Network.Destinations.Count == 0 != (policy.Network.Phases.Count == 0))
        {
            throw new InvalidOperationException(
                "Network capabilities require both destinations and phases, or neither.");
        }

        var duplicateSecret = policy.Secrets.GroupBy(secret => secret.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicateSecret is not null)
            throw new InvalidOperationException($"Duplicate secret capability: '{duplicateSecret.Key}'.");
        foreach (var secret in policy.Secrets)
        {
            if (!Regex.IsMatch(secret.Name, @"^[A-Z_][A-Z0-9_]*$"))
                throw new InvalidOperationException($"Invalid secret capability name: '{secret.Name}'.");
            ValidatePhases(secret.Phases, $"secret capability '{secret.Name}'");
        }

        ValidatePositiveCpu(policy.Resources.CpuLimit, "capabilities.resources.cpu_limit");
        ParseMemoryBytes(policy.Resources.MemoryLimit, "capabilities.resources.memory_limit");
        if (policy.Resources.ProcessLimit <= 0)
            throw new InvalidOperationException("capabilities.resources.process_limit must be positive.");
        if (policy.Resources.WallClockSeconds <= 0)
            throw new InvalidOperationException("capabilities.resources.wall_clock_seconds must be positive.");
    }

    private static void ValidateCapabilityResources(
        ExecutionCapabilityPolicy policy,
        SandboxExecutionProfile sandbox)
    {
        var requestedCpu = ValidatePositiveCpu(sandbox.CpuLimit, "execution.sandbox.cpu_limit");
        var maximumCpu = ValidatePositiveCpu(
            policy.Resources.CpuLimit,
            "capabilities.resources.cpu_limit");
        if (requestedCpu > maximumCpu ||
            ParseMemoryBytes(sandbox.MemoryLimit, "execution.sandbox.memory_limit") >
            ParseMemoryBytes(policy.Resources.MemoryLimit, "capabilities.resources.memory_limit") ||
            sandbox.ProcessLimit > policy.Resources.ProcessLimit ||
            sandbox.WallClockSeconds > policy.Resources.WallClockSeconds)
        {
            throw new InvalidOperationException(
                "execution.sandbox resource limits exceed the authoritative capabilities.resources maxima.");
        }

        if (sandbox.NetworkAccess &&
            (policy.Network.Destinations.Count == 0 || policy.Network.Phases.Count == 0))
        {
            throw new InvalidOperationException(
                "execution.sandbox.network_access requires explicit network destinations and phases.");
        }
    }

    private static void ValidateRequiredProcessCapabilities(
        ExecutionCapabilityPolicy policy,
        VerificationProfile verification,
        IReadOnlyCollection<AcceptanceCriterion> acceptance)
    {
        RequireProcess(policy, "git", "--version", ExecutionCapabilityPhases.BaselineToolProbe);
        RequireProcess(policy, "dotnet", "--version", ExecutionCapabilityPhases.BaselineToolProbe);
        if (verification.Build)
        {
            RequireProcess(policy, "dotnet", "build", ExecutionCapabilityPhases.BaselineBuild);
            RequireProcess(policy, "dotnet", "build", ExecutionCapabilityPhases.CandidateBuild);
        }
        if (verification.UnitTests || verification.IntegrationTests)
        {
            RequireProcess(policy, "dotnet", "test", ExecutionCapabilityPhases.BaselineTest);
            RequireProcess(policy, "dotnet", "test", ExecutionCapabilityPhases.CandidateTest);
        }
        if (acceptance.Any(criterion => criterion.Evidence.Type == AcceptanceEvidenceType.Test))
        {
            RequireProcess(
                policy,
                "dotnet",
                "test",
                ExecutionCapabilityPhases.CandidateAcceptance);
        }
        if (verification.SecurityScan &&
            verification.EffectiveSecurityPolicy.Scanners.Contains(
                SecurityScannerIds.Dependencies,
                StringComparer.Ordinal))
        {
            RequireProcess(
                policy,
                "dotnet",
                "list",
                ExecutionCapabilityPhases.BaselineSecurityScan);
            RequireProcess(
                policy,
                "dotnet",
                "list",
                ExecutionCapabilityPhases.CandidateSecurityScan);
        }
    }

    private static void RequireProcess(
        ExecutionCapabilityPolicy policy,
        string executable,
        string firstArgument,
        string phase)
    {
        if (policy.Processes.Any(rule =>
                rule.Executable.Equals(executable, StringComparison.OrdinalIgnoreCase) &&
                rule.ArgumentPrefix.Count > 0 &&
                rule.ArgumentPrefix[0] == firstArgument &&
                rule.Phases.Contains(phase, StringComparer.Ordinal)))
        {
            return;
        }

        throw new InvalidOperationException(
            $"Capabilities deny required preflight command '{phase}: {executable} {firstArgument}'.");
    }

    private static void ValidateCapabilityWritePath(string path)
    {
        var normalized = path.Trim().Replace('\\', '/');
        if (normalized == "**")
            return;
        if (string.IsNullOrWhiteSpace(normalized) ||
            normalized.StartsWith('/') ||
            Regex.IsMatch(normalized, @"^[a-zA-Z]:/") ||
            normalized.Contains(',') ||
            normalized.Contains(':') ||
            normalized.Any(char.IsControl) ||
            normalized.Split('/').Any(segment => segment is "." or ".." ||
                segment.Equals(".git", StringComparison.OrdinalIgnoreCase)) ||
            !normalized.EndsWith("/**", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                $"Capability write path must be a safe workspace directory pattern ending in '/**': '{path}'.");
        }
        if (normalized.Contains('*') && normalized is not "**/bin/**" and not "**/obj/**")
        {
            throw new InvalidOperationException(
                $"Unsupported wildcard capability write path: '{path}'.");
        }
    }

    private static void ValidatePhases(IReadOnlyCollection<string> phases, string description)
    {
        var known = new HashSet<string>(
        [
            ExecutionCapabilityPhases.BaselineToolProbe,
            ExecutionCapabilityPhases.BaselineBuild,
            ExecutionCapabilityPhases.BaselineTest,
            ExecutionCapabilityPhases.BaselineSecurityScan,
            ExecutionCapabilityPhases.CandidateBuild,
            ExecutionCapabilityPhases.CandidateTest,
            ExecutionCapabilityPhases.CandidateSecurityScan,
            ExecutionCapabilityPhases.CandidateAcceptance
        ], StringComparer.Ordinal);
        if (phases.Count == 0 || phases.Any(phase => !known.Contains(phase)))
            throw new InvalidOperationException($"{description} contains an empty or unknown phase.");
    }

    private static decimal ValidatePositiveCpu(string value, string field)
    {
        if (!decimal.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var cpu) || cpu <= 0)
        {
            throw new InvalidOperationException($"{field} must be a positive invariant decimal.");
        }
        return cpu;
    }

    private static long ParseMemoryBytes(string value, string field)
    {
        var match = Regex.Match(value, @"^([1-9][0-9]*)([kKmMgG]?)$");
        if (!match.Success || !long.TryParse(match.Groups[1].Value, out var amount))
            throw new InvalidOperationException($"{field} must be a positive Docker memory limit.");
        var multiplier = match.Groups[2].Value.ToLowerInvariant() switch
        {
            "k" => 1024L,
            "m" => 1024L * 1024,
            "g" => 1024L * 1024 * 1024,
            _ => 1L
        };
        try
        {
            return checked(amount * multiplier);
        }
        catch (OverflowException)
        {
            throw new InvalidOperationException($"{field} is too large.");
        }
    }

    private static SecurityScanPolicy MapSecurityScanPolicy(SecurityScanPolicyYamlModel? model)
    {
        var policy = new SecurityScanPolicy
        {
            Version = model?.Version ?? model?.version ?? SecurityScanSchema.PolicyVersion,
            Scanners = model?.Scanners ?? model?.scanners ??
                [SecurityScannerIds.Secrets, SecurityScannerIds.Dependencies, SecurityScannerIds.Patterns],
            BlockAtOrAbove = ParseSecuritySeverity(
                model?.BlockAtOrAbove ?? model?.block_at_or_above ?? "error"),
            VulnerabilityDatabaseVersion = model?.VulnerabilityDatabaseVersion ??
                model?.vulnerability_database_version ??
                SecurityScanSchema.VulnerabilityDatabaseVersion,
            Suppressions = (model?.Suppressions ?? model?.suppressions ?? [])
                .Select(suppression => new SecurityScanSuppression
                {
                    Rule = suppression.Rule ?? suppression.rule ?? string.Empty,
                    Path = suppression.Path ?? suppression.path ?? string.Empty,
                    Fingerprint = suppression.Fingerprint ?? suppression.fingerprint ?? string.Empty,
                    Justification = suppression.Justification ??
                        suppression.justification ?? string.Empty
                }).ToList()
        };
        ValidateSecurityScanPolicy(policy);
        return policy;
    }

    private static void ValidateSecurityScanPolicy(SecurityScanPolicy policy)
    {
        if (policy.Version != SecurityScanSchema.PolicyVersion)
            throw new InvalidOperationException($"Unsupported security scan policy version: '{policy.Version}'.");
        if (policy.VulnerabilityDatabaseVersion != SecurityScanSchema.VulnerabilityDatabaseVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported security scan vulnerability database: '{policy.VulnerabilityDatabaseVersion}'.");
        }
        if (policy.Scanners.Count == 0 ||
            policy.Scanners.Distinct(StringComparer.Ordinal).Count() != policy.Scanners.Count ||
            policy.Scanners.Any(scanner => !SecurityScannerIds.All.Contains(scanner)))
        {
            throw new InvalidOperationException(
                "security_policy.scanners must contain unique supported scanner IDs.");
        }

        foreach (var suppression in policy.Suppressions)
        {
            var path = suppression.Path.Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(suppression.Rule) ||
                string.IsNullOrWhiteSpace(path) ||
                path.StartsWith('/') ||
                Regex.IsMatch(path, @"^[a-zA-Z]:/") ||
                path.Split('/').Any(segment => segment is "." or "..") ||
                suppression.Justification.Trim().Length < 10 ||
                !string.IsNullOrEmpty(suppression.Fingerprint) &&
                !Regex.IsMatch(suppression.Fingerprint, @"^sha256:[a-f0-9]{64}$"))
            {
                throw new InvalidOperationException(
                    "Security scan suppressions require a rule, safe relative path, optional sha256 fingerprint, and justification of at least 10 characters.");
            }
        }
    }

    private static Severity ParseSecuritySeverity(string value) => value.Trim().ToLowerInvariant() switch
    {
        "info" => Severity.Info,
        "warning" => Severity.Warning,
        "error" or "high" => Severity.Error,
        "critical" => Severity.Critical,
        _ => throw new InvalidOperationException($"Unknown security scan severity: '{value}'.")
    };

    private static List<AcceptanceCriterion> MapAcceptanceCriteria(
        IReadOnlyList<string> criteria,
        IReadOnlyList<AcceptanceEvidenceYamlModel> mappings)
    {
        var mappedCriteria = new List<AcceptanceCriterion>();
        var consumedMappings = new HashSet<AcceptanceEvidenceYamlModel>();

        foreach (var mapping in mappings)
        {
            var matchingIndexes = Enumerable.Range(0, criteria.Count).Where(index =>
                string.Equals(
                    mapping.Id ?? mapping.id,
                    $"AC-{index + 1:000}",
                    StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    mapping.Criterion ?? mapping.criterion,
                    criteria[index],
                    StringComparison.Ordinal)).ToList();
            if (matchingIndexes.Count != 1)
            {
                throw new InvalidOperationException(
                    $"Acceptance evidence mapping does not match exactly one criterion: " +
                    $"'{mapping.Id ?? mapping.id ?? mapping.Criterion ?? mapping.criterion}'.");
            }
        }

        for (var index = 0; index < criteria.Count; index++)
        {
            var id = $"AC-{index + 1:000}";
            var description = criteria[index];
            var matches = mappings.Where(mapping =>
                string.Equals(mapping.Id ?? mapping.id, id, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(
                    mapping.Criterion ?? mapping.criterion,
                    description,
                    StringComparison.Ordinal)).ToList();
            if (matches.Count > 1)
                throw new InvalidOperationException($"Acceptance criterion '{id}' has duplicate evidence mappings.");

            var mapping = matches.SingleOrDefault();
            if (mapping is not null)
                consumedMappings.Add(mapping);
            mappedCriteria.Add(new AcceptanceCriterion
            {
                Id = id,
                Description = description,
                Required = mapping?.Required ?? mapping?.required ?? true,
                Behavioral = mapping?.Behavioral ?? mapping?.behavioral ?? false,
                Evidence = new AcceptanceEvidenceRequirement
                {
                    Type = ParseAcceptanceEvidenceType(mapping?.Type ?? mapping?.type),
                    Reference = mapping?.Reference ?? mapping?.reference ?? string.Empty,
                    TestPath = mapping?.TestPath ?? mapping?.test_path ?? string.Empty,
                    EquivalentBehavioralEvidence = mapping?.EquivalentBehavioralEvidence
                        ?? mapping?.equivalent_behavioral_evidence
                        ?? false
                }
            });
        }

        var unbound = mappings.Except(consumedMappings).FirstOrDefault();
        if (unbound is not null)
        {
            throw new InvalidOperationException(
                $"Acceptance evidence mapping does not match a criterion: " +
                $"'{unbound.Id ?? unbound.id ?? unbound.Criterion ?? unbound.criterion}'.");
        }

        return mappedCriteria;
    }

    private static AcceptanceEvidenceType ParseAcceptanceEvidenceType(string? value) =>
        value?.Trim().ToLowerInvariant() switch
        {
            null or "" => AcceptanceEvidenceType.None,
            "verifier" => AcceptanceEvidenceType.Verifier,
            "test" => AcceptanceEvidenceType.Test,
            _ => throw new InvalidOperationException($"Unknown acceptance evidence type: '{value}'.")
        };

    private static RiskLevel ParseRiskLevel(string value) => value.ToLowerInvariant() switch
    {
        "low" or "r0" or "r1" => RiskLevel.R1,
        "medium" or "r2" => RiskLevel.R2,
        "high" or "r3" => RiskLevel.R3,
        _ => RiskLevel.R1
    };

    private static bool IsRequired(string value) =>
        value.Equals("required", StringComparison.OrdinalIgnoreCase);

    private static ApprovalLevel ParseApprovalLevel(string value) => value.ToLowerInvariant() switch
    {
        "human" => ApprovalLevel.Human,
        _ => ApprovalLevel.None
    };
}

public class TaskYamlRoot
{
    public TaskYamlModel? Task { get; set; }
    public TaskYamlModel? task { get; set; }
}

public class TaskYamlModel
{
    public string? Id { get; set; }
    public string? id { get; set; }
    public string? Objective { get; set; }
    public string? objective { get; set; }
    public List<string>? Acceptance { get; set; }
    public List<string>? acceptance { get; set; }
    public List<AcceptanceEvidenceYamlModel>? AcceptanceEvidence { get; set; }
    public List<AcceptanceEvidenceYamlModel>? acceptance_evidence { get; set; }
    public ScopeYamlModel? Scope { get; set; }
    public ScopeYamlModel? scope { get; set; }
    public ConstraintsYamlModel? Constraints { get; set; }
    public ConstraintsYamlModel? constraints { get; set; }
    public BudgetYamlModel? Budget { get; set; }
    public BudgetYamlModel? budget { get; set; }
    public ExecutionYamlModel? Execution { get; set; }
    public ExecutionYamlModel? execution { get; set; }
    public CapabilitiesYamlModel? Capabilities { get; set; }
    public CapabilitiesYamlModel? capabilities { get; set; }
    public VerificationYamlModel? Verification { get; set; }
    public VerificationYamlModel? verification { get; set; }
    public ApprovalYamlModel? Approval { get; set; }
    public ApprovalYamlModel? approval { get; set; }
}

public class ScopeYamlModel
{
    public List<string>? Allowed { get; set; }
    public List<string>? allowed { get; set; }
    public List<string>? Forbidden { get; set; }
    public List<string>? forbidden { get; set; }
}

public class ConstraintsYamlModel
{
    public string? SecurityRisk { get; set; }
    public string? security_risk { get; set; }
    public bool DatabaseMigration { get; set; }
    public bool database_migration { get; set; }
    public bool ExternalDependency { get; set; }
    public bool external_dependency { get; set; }
}

public class BudgetYamlModel
{
    public int? Tokens { get; set; }
    public int? tokens { get; set; }
    public decimal? Usd { get; set; }
    public decimal? usd { get; set; }
    public int? Retries { get; set; }
    public int? retries { get; set; }
    public int? WallClockSeconds { get; set; }
    public int? wall_clock_seconds { get; set; }
    public int? MaxFilesChanged { get; set; }
    public int? max_files_changed { get; set; }
}

public class ExecutionYamlModel
{
    public string? WorkingDirectory { get; set; }
    public string? working_directory { get; set; }
    public string? Target { get; set; }
    public string? target { get; set; }
    public string? Runtime { get; set; }
    public string? runtime { get; set; }
    public SandboxYamlModel? Sandbox { get; set; }
    public SandboxYamlModel? sandbox { get; set; }
    public CapabilitiesYamlModel? Capabilities { get; set; }
    public CapabilitiesYamlModel? capabilities { get; set; }
}

public class CapabilitiesYamlModel
{
    public string? Version { get; set; }
    public string? version { get; set; }
    public FileSystemCapabilitiesYamlModel? FileSystem { get; set; }
    public FileSystemCapabilitiesYamlModel? file_system { get; set; }
    public List<ProcessCapabilityYamlModel>? Processes { get; set; }
    public List<ProcessCapabilityYamlModel>? processes { get; set; }
    public NetworkCapabilitiesYamlModel? Network { get; set; }
    public NetworkCapabilitiesYamlModel? network { get; set; }
    public List<SecretCapabilityYamlModel>? Secrets { get; set; }
    public List<SecretCapabilityYamlModel>? secrets { get; set; }
    public ResourceCapabilitiesYamlModel? Resources { get; set; }
    public ResourceCapabilitiesYamlModel? resources { get; set; }
}

public class FileSystemCapabilitiesYamlModel
{
    public List<string>? Read { get; set; }
    public List<string>? read { get; set; }
    public List<string>? Write { get; set; }
    public List<string>? write { get; set; }
}

public class ProcessCapabilityYamlModel
{
    public string? Executable { get; set; }
    public string? executable { get; set; }
    public List<string>? ArgumentPrefix { get; set; }
    public List<string>? argument_prefix { get; set; }
    public List<string>? Phases { get; set; }
    public List<string>? phases { get; set; }
}

public class NetworkCapabilitiesYamlModel
{
    public List<string>? Destinations { get; set; }
    public List<string>? destinations { get; set; }
    public List<string>? Phases { get; set; }
    public List<string>? phases { get; set; }
}

public class SecretCapabilityYamlModel
{
    public string? Name { get; set; }
    public string? name { get; set; }
    public List<string>? Phases { get; set; }
    public List<string>? phases { get; set; }
}

public class ResourceCapabilitiesYamlModel
{
    public string? CpuLimit { get; set; }
    public string? cpu_limit { get; set; }
    public string? MemoryLimit { get; set; }
    public string? memory_limit { get; set; }
    public int? ProcessLimit { get; set; }
    public int? process_limit { get; set; }
    public int? WallClockSeconds { get; set; }
    public int? wall_clock_seconds { get; set; }
}

public class SandboxYamlModel
{
    public string? Image { get; set; }
    public string? image { get; set; }
    public string? CpuLimit { get; set; }
    public string? cpu_limit { get; set; }
    public string? MemoryLimit { get; set; }
    public string? memory_limit { get; set; }
    public int? ProcessLimit { get; set; }
    public int? process_limit { get; set; }
    public int? WallClockSeconds { get; set; }
    public int? wall_clock_seconds { get; set; }
    public bool? NetworkAccess { get; set; }
    public bool? network_access { get; set; }
}

public class VerificationYamlModel
{
    public string? Build { get; set; }
    public string? build { get; set; }
    public string? UnitTests { get; set; }
    public string? unit_tests { get; set; }
    public string? IntegrationTests { get; set; }
    public string? integration_tests { get; set; }
    public string? Scope { get; set; }
    public string? scope { get; set; }
    public string? SecurityScan { get; set; }
    public string? security_scan { get; set; }
    public string? Architecture { get; set; }
    public string? architecture { get; set; }
    public string? CriticalSemanticFailures { get; set; }
    public string? critical_semantic_failures { get; set; }
    public List<string>? RequiredSemanticVerifiers { get; set; }
    public List<string>? required_semantic_verifiers { get; set; }
    public SecurityScanPolicyYamlModel? SecurityPolicy { get; set; }
    public SecurityScanPolicyYamlModel? security_policy { get; set; }
}

public class SecurityScanPolicyYamlModel
{
    public string? Version { get; set; }
    public string? version { get; set; }
    public List<string>? Scanners { get; set; }
    public List<string>? scanners { get; set; }
    public string? BlockAtOrAbove { get; set; }
    public string? block_at_or_above { get; set; }
    public string? VulnerabilityDatabaseVersion { get; set; }
    public string? vulnerability_database_version { get; set; }
    public List<SecurityScanSuppressionYamlModel>? Suppressions { get; set; }
    public List<SecurityScanSuppressionYamlModel>? suppressions { get; set; }
}

public class SecurityScanSuppressionYamlModel
{
    public string? Rule { get; set; }
    public string? rule { get; set; }
    public string? Path { get; set; }
    public string? path { get; set; }
    public string? Fingerprint { get; set; }
    public string? fingerprint { get; set; }
    public string? Justification { get; set; }
    public string? justification { get; set; }
}

public class AcceptanceEvidenceYamlModel
{
    public string? Id { get; set; }
    public string? id { get; set; }
    public string? Criterion { get; set; }
    public string? criterion { get; set; }
    public string? Type { get; set; }
    public string? type { get; set; }
    public string? Reference { get; set; }
    public string? reference { get; set; }
    public string? TestPath { get; set; }
    public string? test_path { get; set; }
    public bool? EquivalentBehavioralEvidence { get; set; }
    public bool? equivalent_behavioral_evidence { get; set; }
    public bool? Required { get; set; }
    public bool? required { get; set; }
    public bool? Behavioral { get; set; }
    public bool? behavioral { get; set; }
}

public class ApprovalYamlModel
{
    public string? Production { get; set; }
    public string? production { get; set; }
}
