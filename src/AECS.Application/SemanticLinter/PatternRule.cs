using System.Text.RegularExpressions;

namespace AECS.Application.SemanticLinter;

public class PatternRule
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public PatternType Type { get; init; }
    public string Pattern { get; init; } = string.Empty;
    public string ExpectedPattern { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty; // namespace pattern
    public RuleSeverity Severity { get; init; } = RuleSeverity.Warning;
}

public enum PatternType
{
    NamingConvention,
    InterfaceImplementation,
    BaseClassInheritance,
    MethodSignature,
    FileStructure,
    UsingPattern
}

public class PatternViolation
{
    public string RuleId { get; init; } = string.Empty;
    public string RuleName { get; init; } = string.Empty;
    public string FilePath { get; init; } = string.Empty;
    public string ViolationDetail { get; init; } = string.Empty;
    public RuleSeverity Severity { get; init; }
    public string ExpectedPattern { get; init; } = string.Empty;
    public string ActualPattern { get; init; } = string.Empty;
}

public class EB002Result
{
    public bool HasViolations => Violations.Count > 0;
    public List<PatternViolation> Violations { get; init; } = [];
    public int FilesScanned { get; init; }
    public int RulesChecked { get; init; }
    public Dictionary<string, int> PatternStats { get; init; } = new();
}

public static class DefaultPatternRules
{
    public static List<PatternRule> GetCommonPatternRules()
    {
        return
        [
            // Services should implement an interface
            new PatternRule
            {
                Id = "EB002-SERVICE-INTERFACE",
                Name = "Service Interface Implementation",
                Description = "Service classes should implement a corresponding interface (e.g., IMyService)",
                Type = PatternType.InterfaceImplementation,
                Pattern = @"class\s+(\w+Service)\b",
                ExpectedPattern = @"interface\s+I\1",
                Scope = "Application",
                Severity = RuleSeverity.Warning
            },

            // Repositories should implement an interface
            new PatternRule
            {
                Id = "EB002-REPO-INTERFACE",
                Name = "Repository Interface Implementation",
                Description = "Repository classes should implement a corresponding interface (e.g., IMyRepository)",
                Type = PatternType.InterfaceImplementation,
                Pattern = @"class\s+(\w+Repository)\b",
                ExpectedPattern = @"interface\s+I\1",
                Scope = "Infrastructure",
                Severity = RuleSeverity.Warning
            },

            // Controllers should use dependency injection
            new PatternRule
            {
                Id = "EB002-CONTROLLER-DI",
                Name = "Controller Dependency Injection",
                Description = "Controllers should use constructor dependency injection, not direct instantiation",
                Type = PatternType.MethodSignature,
                Pattern = @"new\s+\w+Service\s*\(",
                ExpectedPattern = "Constructor injection via ILogger<T>, IService, etc.",
                Scope = "Controllers",
                Severity = RuleSeverity.Error
            },

            // DTOs should be records or have specific suffix
            new PatternRule
            {
                Id = "EB002-DTO-NAMING",
                Name = "DTO Naming Convention",
                Description = "DTOs should end with 'Dto', 'Request', 'Response', or 'Command'",
                Type = PatternType.NamingConvention,
                Pattern = @"class\s+(\w+)\s*{[^}]*(?:public\s+\w+\s+\w+\s*\{)",
                ExpectedPattern = "ClassName should end with Dto, Request, Response, or Command",
                Scope = "DTOs",
                Severity = RuleSeverity.Info
            },

            // Async methods should end with Async
            new PatternRule
            {
                Id = "EB002-ASYNC-NAMING",
                Name = "Async Method Naming",
                Description = "Async methods should end with 'Async' suffix",
                Type = PatternType.NamingConvention,
                Pattern = @"(?:public|private|protected|internal)\s+(?:async\s+)?Task\S*\s+(\w+)\s*\(",
                ExpectedPattern = "Async methods should have 'Async' suffix",
                Scope = "",
                Severity = RuleSeverity.Warning
            },

            // Handlers should follow CQRS pattern
            new PatternRule
            {
                Id = "EB002-HANDLER-CQRS",
                Name = "CQRS Handler Pattern",
                Description = "Command/Query handlers should implement IRequestHandler<T>",
                Type = PatternType.InterfaceImplementation,
                Pattern = @"class\s+(\w+Handler)\b",
                ExpectedPattern = @"IRequestHandler<",
                Scope = "Handlers",
                Severity = RuleSeverity.Warning
            }
        ];
    }
}
