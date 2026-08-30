namespace AECS.Domain.Models;

public class ScopeDefinition
{
    public List<string> Allowed { get; init; } = [];
    public List<string> Forbidden { get; init; } = [];
}
