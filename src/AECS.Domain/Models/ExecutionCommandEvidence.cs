namespace AECS.Domain.Models;

public sealed class ExecutionCommandEvidence
{
    public Guid Id { get; init; } = Guid.NewGuid();
    public string FileName { get; init; } = string.Empty;
    public List<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = string.Empty;
    public int ExitCode { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public TimeSpan Duration { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
}
