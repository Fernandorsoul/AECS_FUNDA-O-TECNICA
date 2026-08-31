namespace AECS.Domain.Models;

public class ProcessExecutionRequest
{
    public string FileName { get; init; } = string.Empty;
    public IReadOnlyList<string> Arguments { get; init; } = [];
    public string WorkingDirectory { get; init; } = string.Empty;
    public TimeSpan Timeout { get; init; } = TimeSpan.FromMinutes(5);
    public string Phase { get; init; } = string.Empty;
}

public class ProcessExecutionResult
{
    public int ExitCode { get; init; }
    public string StandardOutput { get; init; } = string.Empty;
    public string StandardError { get; init; } = string.Empty;
    public TimeSpan Duration { get; init; }
    public bool TimedOut { get; init; }
    public bool Cancelled { get; init; }
    public ExecutionEnvironmentEvidence? Environment { get; init; }

    public bool Succeeded => !TimedOut && !Cancelled && ExitCode == 0;
}
