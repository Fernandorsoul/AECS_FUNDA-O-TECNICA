using AECS.Domain.Enums;

namespace AECS.Domain.Models;

public class TaskConstraints
{
    public RiskLevel SecurityRisk { get; init; }
    public bool DatabaseMigration { get; init; }
    public bool ExternalDependency { get; init; }
}
