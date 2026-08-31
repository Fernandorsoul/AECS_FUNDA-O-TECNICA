using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace AECS.Domain.Models;

public static class ExecutionCapabilityPhases
{
    public const string BaselineToolProbe = "baseline.tool-probe";
    public const string BaselineBuild = "baseline.build";
    public const string BaselineTest = "baseline.test";
    public const string CandidateBuild = "candidate.build";
    public const string CandidateTest = "candidate.test";
    public const string CandidateAcceptance = "candidate.acceptance";
}

public sealed class ExecutionCapabilityPolicy
{
    public const string CurrentVersion = "aecs.capabilities/v1";
    public const string TaskContractAuthority = "task-contract";
    public const string LegacyVersion = "aecs.capabilities/legacy-v0";

    public string Version { get; init; } = CurrentVersion;
    public string Authority { get; init; } = TaskContractAuthority;
    public FileSystemCapabilities FileSystem { get; init; } = new();
    public List<ProcessCapabilityRule> Processes { get; init; } = [];
    public NetworkCapabilities Network { get; init; } = new();
    public List<SecretCapability> Secrets { get; init; } = [];
    public ResourceCapabilities Resources { get; init; } = new();

    public static ExecutionCapabilityPolicy RestrictiveDefault() => new()
    {
        FileSystem = new FileSystemCapabilities
        {
            Read = ["**"],
            Write = ["**/bin/**", "**/obj/**", ".aecs-verification/**"]
        },
        Processes =
        [
            Rule("git", ["--version"], ExecutionCapabilityPhases.BaselineToolProbe),
            Rule("dotnet", ["--version"], ExecutionCapabilityPhases.BaselineToolProbe),
            Rule("dotnet", ["build"],
                ExecutionCapabilityPhases.BaselineBuild,
                ExecutionCapabilityPhases.CandidateBuild),
            Rule("dotnet", ["test"],
                ExecutionCapabilityPhases.BaselineTest,
                ExecutionCapabilityPhases.CandidateTest,
                ExecutionCapabilityPhases.CandidateAcceptance)
        ],
        Resources = new ResourceCapabilities()
    };

    public static ExecutionCapabilityPolicy LegacyCompatibility(bool networkAccess) => new()
    {
        Version = LegacyVersion,
        Authority = "authenticated-legacy-evidence",
        FileSystem = new FileSystemCapabilities { Read = ["**"], Write = ["**"] },
        Processes = [new ProcessCapabilityRule
        {
            Executable = "*",
            ArgumentPrefix = [],
            Phases = ["*"]
        }],
        Network = new NetworkCapabilities
        {
            Destinations = networkAccess ? ["*"] : [],
            Phases = networkAccess ? ["*"] : []
        },
        Resources = new ResourceCapabilities
        {
            CpuLimit = "unlimited",
            MemoryLimit = "unlimited",
            ProcessLimit = int.MaxValue,
            WallClockSeconds = int.MaxValue
        }
    };

    private static ProcessCapabilityRule Rule(
        string executable,
        List<string> argumentPrefix,
        params string[] phases) => new()
        {
            Executable = executable,
            ArgumentPrefix = argumentPrefix,
            Phases = phases.ToList()
        };
}

public sealed class FileSystemCapabilities
{
    public List<string> Read { get; init; } = [];
    public List<string> Write { get; init; } = [];
}

public sealed class ProcessCapabilityRule
{
    public string Executable { get; init; } = string.Empty;
    public List<string> ArgumentPrefix { get; init; } = [];
    public List<string> Phases { get; init; } = [];
}

public sealed class NetworkCapabilities
{
    public List<string> Destinations { get; init; } = [];
    public List<string> Phases { get; init; } = [];
}

public sealed class SecretCapability
{
    public string Name { get; init; } = string.Empty;
    public List<string> Phases { get; init; } = [];
}

public sealed class ResourceCapabilities
{
    public string CpuLimit { get; init; } = "1.0";
    public string MemoryLimit { get; init; } = "512m";
    public int ProcessLimit { get; init; } = 128;
    public int WallClockSeconds { get; init; } = 120;
}

public static class ExecutionCapabilityPolicyFingerprint
{
    public static string Create(ExecutionCapabilityPolicy policy) =>
        "sha256:" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(policy)))).ToLowerInvariant();
}
