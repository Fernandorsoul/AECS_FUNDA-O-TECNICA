namespace AECS.Application.SemanticLinter;

public class ArchitectureRule
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public string SourceLayer { get; init; } = string.Empty;
    public List<string> ForbiddenDependencies { get; init; } = [];
    public List<string> AllowedDependencies { get; init; } = [];
    public RuleSeverity Severity { get; init; } = RuleSeverity.Error;
}

public enum RuleSeverity
{
    Info,
    Warning,
    Error,
    Critical
}

public class ArchitectureViolation
{
    public string RuleId { get; init; } = string.Empty;
    public string RuleName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string ViolationDetail { get; init; } = string.Empty;
    public RuleSeverity Severity { get; init; }
    public string SourceNamespace { get; init; } = string.Empty;
    public string ForbiddenDependency { get; init; } = string.Empty;
}

public static class DefaultArchitectureRules
{
    public static List<ArchitectureRule> GetCleanArchitectureRules()
    {
        return
        [
            // Domain cannot depend on anything else
            new ArchitectureRule
            {
                Id = "EB001-DOMAIN",
                Name = "Domain Layer Independence",
                Description = "Domain layer must not depend on Application, Infrastructure, or API layers",
                SourceLayer = "AECS.Domain",
                ForbiddenDependencies = ["AECS.Application", "AECS.Infrastructure", "AECS.Api"],
                Severity = RuleSeverity.Critical
            },

            // Application can depend on Domain, but not Infrastructure or API
            new ArchitectureRule
            {
                Id = "EB001-APP",
                Name = "Application Layer Dependencies",
                Description = "Application layer must not depend on Infrastructure or API layers",
                SourceLayer = "AECS.Application",
                ForbiddenDependencies = ["AECS.Infrastructure", "AECS.Api"],
                AllowedDependencies = ["AECS.Domain"],
                Severity = RuleSeverity.Error
            },

            // Infrastructure can depend on Domain, but not Application or API
            new ArchitectureRule
            {
                Id = "EB001-INFRA",
                Name = "Infrastructure Layer Dependencies",
                Description = "Infrastructure layer must not depend on Application or API layers",
                SourceLayer = "AECS.Infrastructure",
                ForbiddenDependencies = ["AECS.Application", "AECS.Api"],
                AllowedDependencies = ["AECS.Domain"],
                Severity = RuleSeverity.Error
            },

            // Controllers should not access DbContext directly
            new ArchitectureRule
            {
                Id = "EB001-CONTROLLER-DB",
                Name = "Controller Direct DB Access",
                Description = "Controllers should not access DbContext directly — use Application services",
                SourceLayer = "AECS.Api.Controllers",
                ForbiddenDependencies = ["DbContext", "AgroPlusDbContext", "AecsDbContext"],
                Severity = RuleSeverity.Error
            },

            // Domain cannot use EF Core or any ORM
            new ArchitectureRule
            {
                Id = "EB001-DOMAIN-ORM",
                Name = "Domain ORM Independence",
                Description = "Domain layer must not reference Entity Framework or any ORM",
                SourceLayer = "AECS.Domain",
                ForbiddenDependencies = ["Microsoft.EntityFrameworkCore", "System.Data"],
                Severity = RuleSeverity.Critical
            },

            // Domain cannot use HTTP or networking
            new ArchitectureRule
            {
                Id = "EB001-DOMAIN-NETWORK",
                Name = "Domain Network Independence",
                Description = "Domain layer must not use HTTP clients or networking",
                SourceLayer = "AECS.Domain",
                ForbiddenDependencies = ["System.Net.Http", "HttpClient"],
                Severity = RuleSeverity.Critical
            }
        ];
    }
}
