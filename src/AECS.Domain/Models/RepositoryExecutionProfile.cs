namespace AECS.Domain.Models;

public sealed class RepositoryExecutionProfile
{
    public string WorkingDirectory { get; init; } = ".";
    public string Target { get; init; } = string.Empty;
}
