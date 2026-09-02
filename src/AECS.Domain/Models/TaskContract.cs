using AECS.Domain.Enums;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AECS.Domain.Models;

public static class TaskContractSchema
{
    public const string CurrentVersion = "aecs.task-contract/v1";
    public const string LegacyVersion = "aecs.task-contract/legacy-v0";
}

public class TaskContract
{
    // Empty values preserve the authenticated JSON representation of evidence that
    // predates the versioned contract. Runtime inputs are always upgraded and sealed.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? SchemaVersion { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? ContractFingerprint { get; init; }

    public string Id { get; init; } = string.Empty;
    public string Objective { get; init; } = string.Empty;
    public List<string> AcceptanceCriteria { get; init; } = [];
    public List<AcceptanceCriterion> AcceptanceRequirements { get; init; } = [];
    public ScopeDefinition Scope { get; init; } = new();
    public TaskConstraints Constraints { get; init; } = new();
    public ExecutionBudget Budget { get; init; } = ExecutionBudget.Default;
    public RepositoryExecutionProfile Execution { get; init; } = new();
    public VerificationProfile Verification { get; init; } = new();
    public ApprovalPolicy Approval { get; init; } = new();
    public TaskState Status { get; set; } = TaskState.Created;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;
}

public static class TaskContractIntegrity
{
    public const int MaximumIdLength = 128;
    public const int MaximumObjectiveLength = 4000;

    public static TaskContract Seal(TaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (contract.SchemaVersion is not null &&
            contract.SchemaVersion != TaskContractSchema.CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Unsupported TaskContract schema: '{contract.SchemaVersion}'.");
        }
        if (contract.SchemaVersion is null && contract.ContractFingerprint is not null)
        {
            throw new InvalidOperationException(
                "TaskContract fingerprint cannot exist without a schema version.");
        }

        var current = Copy(
            contract,
            TaskContractSchema.CurrentVersion,
            string.Empty);
        ValidateContent(current);
        return Copy(
            current,
            TaskContractSchema.CurrentVersion,
            TaskContractFingerprint.Create(current));
    }

    public static bool IsLegacy(TaskContract contract) =>
        contract.SchemaVersion is null &&
        contract.ContractFingerprint is null;

    public static void ValidateForEvidence(TaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        if (IsLegacy(contract))
            return;

        ValidateContent(contract);
        if (contract.SchemaVersion != TaskContractSchema.CurrentVersion ||
            !IsSha256(contract.ContractFingerprint) ||
            !string.Equals(
                contract.ContractFingerprint,
                TaskContractFingerprint.Create(contract),
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "TaskContract fingerprint is invalid or uses an unsupported schema.");
        }
    }

    private static void ValidateContent(TaskContract contract)
    {
        if (contract.SchemaVersion != TaskContractSchema.CurrentVersion ||
            string.IsNullOrWhiteSpace(contract.Id) ||
            contract.Id.Length > MaximumIdLength ||
            string.IsNullOrWhiteSpace(contract.Objective) ||
            contract.Objective.Length > MaximumObjectiveLength ||
            contract.AcceptanceCriteria is null ||
            contract.AcceptanceRequirements is null ||
            contract.Scope is null ||
            contract.Scope.Allowed is null ||
            contract.Scope.Forbidden is null ||
            contract.Constraints is null ||
            !Enum.IsDefined(contract.Constraints.SecurityRisk) ||
            contract.Budget is null ||
            contract.Execution is null ||
            contract.Verification is null ||
            contract.Approval is null ||
            !Enum.IsDefined(contract.Approval.Production))
        {
            throw new InvalidOperationException(
                "TaskContract is incomplete or uses an unsupported schema.");
        }
    }

    private static bool IsSha256(string? value) =>
        value is not null &&
        value.Length == 71 &&
        value.StartsWith("sha256:", StringComparison.Ordinal) &&
        value[7..].All(character => Uri.IsHexDigit(character) && !char.IsUpper(character));

    private static TaskContract Copy(
        TaskContract source,
        string schemaVersion,
        string? fingerprint) => new()
        {
            SchemaVersion = schemaVersion,
            ContractFingerprint = fingerprint,
            Id = source.Id,
            Objective = source.Objective,
            AcceptanceCriteria = source.AcceptanceCriteria,
            AcceptanceRequirements = source.AcceptanceRequirements,
            Scope = source.Scope,
            Constraints = source.Constraints,
            Budget = source.Budget,
            Execution = source.Execution,
            Verification = source.Verification,
            Approval = source.Approval,
            Status = source.Status,
            CreatedAt = source.CreatedAt
        };
}

public static class TaskContractFingerprint
{
    public static string Create(TaskContract contract)
    {
        ArgumentNullException.ThrowIfNull(contract);
        var canonical = new
        {
            contract.SchemaVersion,
            contract.Id,
            contract.Objective,
            contract.AcceptanceCriteria,
            contract.AcceptanceRequirements,
            contract.Scope,
            contract.Constraints,
            contract.Budget,
            contract.Execution,
            contract.Verification,
            contract.Approval
        };
        var bytes = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(canonical));
        return "sha256:" + Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    }
}
