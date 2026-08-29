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
        var verification = model.Verification ?? model.verification;
        var approval = model.Approval ?? model.approval;

        return new TaskContract
        {
            Id = model.Id ?? model.id ?? string.Empty,
            Objective = model.Objective ?? model.objective ?? string.Empty,
            AcceptanceCriteria = model.Acceptance ?? model.acceptance ?? [],
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
            Budget = new ExecutionBudget
            {
                MaxTokens = budget?.Tokens ?? budget?.tokens ?? 60000,
                MaxCostUsd = budget?.Usd ?? budget?.usd ?? 0.20m,
                MaxRetries = budget?.Retries ?? budget?.retries ?? 1,
                MaxDurationSeconds = budget?.WallClockSeconds ?? budget?.wall_clock_seconds ?? 120,
                MaxFilesChanged = budget?.MaxFilesChanged ?? budget?.max_files_changed ?? 10
            },
            Verification = new VerificationProfile
            {
                Build = IsRequired(verification?.Build ?? verification?.build ?? "required"),
                UnitTests = IsRequired(verification?.UnitTests ?? verification?.unit_tests ?? "required"),
                IntegrationTests = IsRequired(verification?.IntegrationTests ?? verification?.integration_tests ?? "optional"),
                Scope = IsRequired(verification?.Scope ?? verification?.scope ?? "required"),
                SecurityScan = IsRequired(verification?.SecurityScan ?? verification?.security_scan ?? "optional"),
                Architecture = IsRequired(verification?.Architecture ?? verification?.architecture ?? "optional")
            },
            Approval = new ApprovalPolicy
            {
                Production = ParseApprovalLevel(approval?.Production ?? approval?.production ?? "none")
            },
            Status = TaskState.ContractReady,
            CreatedAt = DateTime.UtcNow
        };
    }

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
    public ScopeYamlModel? Scope { get; set; }
    public ScopeYamlModel? scope { get; set; }
    public ConstraintsYamlModel? Constraints { get; set; }
    public ConstraintsYamlModel? constraints { get; set; }
    public BudgetYamlModel? Budget { get; set; }
    public BudgetYamlModel? budget { get; set; }
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
}

public class ApprovalYamlModel
{
    public string? Production { get; set; }
    public string? production { get; set; }
}
