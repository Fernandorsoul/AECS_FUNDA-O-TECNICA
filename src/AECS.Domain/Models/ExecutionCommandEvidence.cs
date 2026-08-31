using System.Text.Json.Serialization;

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

    // Omitted for legacy signed records so their canonical payload stays verifiable.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionEnvironmentEvidence? Environment { get; init; }
}

public sealed class ExecutionEnvironmentEvidence
{
    public string Runtime { get; init; } = string.Empty;
    public string RuntimeVersion { get; init; } = string.Empty;
    public string Image { get; init; } = string.Empty;
    public string ImageDigest { get; init; } = string.Empty;
    public string NetworkMode { get; init; } = string.Empty;
    public string CpuLimit { get; init; } = string.Empty;
    public string MemoryLimit { get; init; } = string.Empty;
    public int ProcessLimit { get; init; }
    public int WallClockLimitSeconds { get; init; }
    public string WorkspaceMount { get; init; } = string.Empty;
    public bool DevelopmentHostOverride { get; init; }
}
