namespace AECS.Domain.Models;

public class CandidateChangeSet
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string TaskId { get; init; } = string.Empty;
    public string AgentRunId { get; init; } = string.Empty;
    public string BaselineCommit { get; init; } = string.Empty;
    public List<string> AddedFiles { get; init; } = [];
    public List<string> ModifiedFiles { get; init; } = [];
    public List<string> DeletedFiles { get; init; } = [];
    public string Diff { get; init; } = string.Empty;
    public string DiffHash { get; init; } = string.Empty;
    public DateTime CreatedAt { get; init; } = DateTime.UtcNow;

    public IReadOnlyList<string> ChangedFiles => AddedFiles
        .Concat(ModifiedFiles)
        .Concat(DeletedFiles)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
        .ToList();

    public bool HasChanges => ChangedFiles.Count > 0 && !string.IsNullOrWhiteSpace(Diff);
}
