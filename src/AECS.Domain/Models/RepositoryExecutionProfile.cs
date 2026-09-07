using System.Text.Json.Serialization;

namespace AECS.Domain.Models;

public sealed class RepositoryExecutionProfile
{
    public const string DockerRuntime = "docker";
    public const string HostRuntime = "host";

    public string WorkingDirectory { get; init; } = ".";
    public string Target { get; init; } = string.Empty;

    // Nullable and omitted to preserve the canonical representation of evidence that
    // predates the sandbox policy. New parsed contracts always set this explicitly.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Runtime { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public SandboxExecutionProfile? Sandbox { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public ExecutionCapabilityPolicy? Capabilities { get; init; }

    // Null denotes the authenticated legacy aggregate `Tests` gate. New independent
    // suites opt into the explicitly versioned matrix.
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public TestSuiteMatrix? TestSuites { get; init; }

    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public RepositorySnapshotProfile? RepositorySnapshot { get; init; }

    [JsonIgnore]
    public string EffectiveRuntime => string.IsNullOrWhiteSpace(Runtime)
        ? DockerRuntime
        : Runtime.Trim().ToLowerInvariant();

    [JsonIgnore]
    public ExecutionCapabilityPolicy EffectiveCapabilities =>
        Capabilities ?? (TestSuites is null
            ? ExecutionCapabilityPolicy.RestrictiveDefault()
            : ExecutionCapabilityPolicy.TestSuitesDefault(TestSuites));
}

public sealed class SandboxExecutionProfile
{
    public const string DefaultImage =
        "mcr.microsoft.com/dotnet/sdk:10.0@sha256:4beef5b8919dcaa2dc924233bd069257e883cc7a061e09088a97d152d6a48510";

    public string Image { get; init; } = DefaultImage;
    public string CpuLimit { get; init; } = "1.0";
    public string MemoryLimit { get; init; } = "512m";
    public int ProcessLimit { get; init; } = 128;
    public int WallClockSeconds { get; init; } = 120;
    public bool NetworkAccess { get; init; }
}
