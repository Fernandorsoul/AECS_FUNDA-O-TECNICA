namespace AECS.Domain.Models;

public class BaselineSnapshot
{
    public string Commit { get; init; } = string.Empty;
    public string Branch { get; init; } = string.Empty;
    public string GitStatus { get; init; } = string.Empty;
    public string RepositoryPath { get; init; } = string.Empty;
    public DateTime CapturedAt { get; init; } = DateTime.UtcNow;

    public bool IsClean => string.IsNullOrWhiteSpace(GitStatus);
}
