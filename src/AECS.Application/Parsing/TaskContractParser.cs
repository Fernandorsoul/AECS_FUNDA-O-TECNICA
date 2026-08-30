using AECS.Domain.Enums;
using AECS.Domain.Models;
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

        return new TaskContract
        {
            Id = model.Id ?? model.id ?? string.Empty,
            Objective = model.Objective ?? model.objective ?? string.Empty,
            AcceptanceCriteria = acceptance,
            AcceptanceRequirements = MapAcceptanceCriteria(acceptance, acceptanceEvidence),
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
                Target = execution?.Target ?? execution?.target ?? string.Empty
            },
            Verification = new VerificationProfile
            {
                Build = IsRequired(verification?.Build ?? verification?.build ?? "required"),
                UnitTests = IsRequired(verification?.UnitTests ?? verification?.unit_tests ?? "required"),
                IntegrationTests = IsRequired(verification?.IntegrationTests ?? verification?.integration_tests ?? "optional"),
                Scope = IsRequired(verification?.Scope ?? verification?.scope ?? "required"),
                SecurityScan = IsRequired(verification?.SecurityScan ?? verification?.security_scan ?? "optional"),
                Architecture = IsRequired(verification?.Architecture ?? verification?.architecture ?? "optional"),
                BlockCriticalSemanticFailures = IsRequired(
                    verification?.CriticalSemanticFailures
                    ?? verification?.critical_semantic_failures
                    ?? "required"),
                RequiredSemanticVerifiers = verification?.RequiredSemanticVerifiers
                    ?? verification?.required_semantic_verifiers
                    ?? []
            },
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
