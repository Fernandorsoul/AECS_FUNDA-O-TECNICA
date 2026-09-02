namespace AECS.Application.SemanticLinter;

public class PatternRule
{
    public string Id { get; init; } = string.Empty;
    public string Name { get; init; } = string.Empty;
    public string Description { get; init; } = string.Empty;
    public PatternType Type { get; init; }
    /// <summary>
    /// Legacy textual hint retained for contract compatibility. EB002 does not consume it as
    /// semantic authority; structural fields and resolved graph relations drive enforcement.
    /// </summary>
    [Obsolete("Text patterns are non-authoritative; configure structural semantic fields instead.")]
    public string Pattern { get; init; } = string.Empty;
    public string ExpectedPattern { get; init; } = string.Empty;
    public string Scope { get; init; } = string.Empty; // namespace pattern
    public RuleSeverity Severity { get; init; } = RuleSeverity.Warning;
    public string SymbolKind { get; init; } = string.Empty;
    public string NameSuffix { get; init; } = string.Empty;
    public string RequiredImplementedType { get; init; } = string.Empty;
    public string ProhibitedConstructedTypeSuffix { get; init; } = string.Empty;
    public List<string> AllowedNameSuffixes { get; init; } = [];
    public bool RequireAsyncSuffix { get; init; }
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
                SymbolKind = "type",
                NameSuffix = "Service",
                RequiredImplementedType = "I{Name}",
                ExpectedPattern = "Resolved implements edge to I{Name}",
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
                SymbolKind = "type",
                NameSuffix = "Repository",
                RequiredImplementedType = "I{Name}",
                ExpectedPattern = "Resolved implements edge to I{Name}",
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
                SymbolKind = "member",
                ProhibitedConstructedTypeSuffix = "Service",
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
                SymbolKind = "type",
                AllowedNameSuffixes = ["Dto", "Request", "Response", "Command"],
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
                SymbolKind = "member",
                RequireAsyncSuffix = true,
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
                SymbolKind = "type",
                NameSuffix = "Handler",
                RequiredImplementedType = "IRequestHandler",
                ExpectedPattern = "Resolved implements edge to IRequestHandler<T>",
                Scope = "Handlers",
                Severity = RuleSeverity.Warning
            }
        ];
    }
}
