using System.Globalization;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Sandbox;

internal sealed class DockerCapabilityPolicy
{
    private static readonly string[] ProjectExtensions =
        [".sln", ".slnx", ".csproj", ".fsproj", ".vbproj"];
    private readonly string _workspace;
    private readonly SandboxExecutionProfile _sandbox;
    private readonly ExecutionCapabilityPolicy _policy;

    public DockerCapabilityPolicy(
        string workspace,
        SandboxExecutionProfile sandbox,
        ExecutionCapabilityPolicy policy)
    {
        _workspace = workspace;
        _sandbox = sandbox;
        _policy = policy;
        Validate(policy, sandbox);
        PolicyHash = ExecutionCapabilityPolicyFingerprint.Create(policy);
    }

    public string PolicyHash { get; }

    public DockerCapabilityDecision Authorize(ProcessExecutionRequest request)
    {
        if (_policy.Version == ExecutionCapabilityPolicy.LegacyVersion)
        {
            return DockerCapabilityDecision.Allowed(
                request.Phase,
                rootWritable: true,
                networkMode: _sandbox.NetworkAccess ? "bridge" : "none",
                [],
                new Dictionary<string, string>(StringComparer.Ordinal),
                ["legacy:authenticated-compatibility"]);
        }

        if (string.IsNullOrWhiteSpace(request.Phase))
            return Denied(request.Phase, "phase:missing");

        var executable = Path.GetFileName(request.FileName);
        var processRule = _policy.Processes.FirstOrDefault(rule =>
            string.Equals(rule.Executable, executable, StringComparison.OrdinalIgnoreCase) &&
            rule.Phases.Contains(request.Phase, StringComparer.Ordinal) &&
            StartsWith(request.Arguments, rule.ArgumentPrefix));
        if (processRule is null)
        {
            return Denied(
                request.Phase,
                $"process:{executable}:phase-or-arguments-not-authorized");
        }

        List<DockerWritableMount> writableMounts;
        try
        {
            writableMounts = ResolveWritableMounts();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or
                   InvalidOperationException or ArgumentException or NotSupportedException)
        {
            return Denied(request.Phase, $"filesystem:writable-mount-denied:{ex.Message}");
        }
        var rootWritable = _policy.FileSystem.Write.Contains("**", StringComparer.Ordinal);
        var networkMode = "none";
        if (_sandbox.NetworkAccess)
        {
            if (!_policy.Network.Phases.Contains(request.Phase, StringComparer.Ordinal))
                return Denied(request.Phase, "network:phase-not-authorized");
            if (!_policy.Network.Destinations.Contains("*", StringComparer.Ordinal))
            {
                return Denied(
                    request.Phase,
                    "network:destination-scoped-egress-unavailable-failed-closed");
            }
            networkMode = "bridge";
        }

        var secrets = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var secret in _policy.Secrets.Where(secret =>
                     secret.Phases.Contains(request.Phase, StringComparer.Ordinal)))
        {
            var value = Environment.GetEnvironmentVariable(secret.Name);
            if (string.IsNullOrEmpty(value))
                return Denied(request.Phase, $"secret:{secret.Name}:source-unavailable");
            secrets.Add(secret.Name, value);
        }

        var grants = new List<string>
        {
            $"process:{executable}:{string.Join(' ', processRule.ArgumentPrefix)}",
            "filesystem:read:workspace/**",
            $"network:{networkMode}",
            $"resource:cpu:{_sandbox.CpuLimit}",
            $"resource:memory:{_sandbox.MemoryLimit}",
            $"resource:pids:{_sandbox.ProcessLimit}",
            $"resource:wall-clock:{_sandbox.WallClockSeconds}s"
        };
        grants.AddRange(_policy.FileSystem.Write.Select(path => $"filesystem:write:{path}"));
        grants.AddRange(secrets.Keys.Select(name => $"secret:{name}"));
        if (networkMode == "bridge")
        {
            grants.AddRange(_policy.Network.Destinations.Select(destination =>
                $"network:destination:{destination}"));
        }

        return DockerCapabilityDecision.Allowed(
            request.Phase,
            rootWritable,
            networkMode,
            writableMounts,
            secrets,
            grants);
    }

    public ExecutionCapabilityEvidence Evidence(DockerCapabilityDecision decision) => new()
    {
        PolicyVersion = _policy.Version,
        Authority = _policy.Authority,
        PolicyHash = PolicyHash,
        Phase = decision.Phase,
        Granted = decision.Grants,
        Denied = decision.Denials,
        InjectedSecrets = decision.Secrets.Keys.Order(StringComparer.Ordinal).ToList()
    };

    public static void Validate(
        ExecutionCapabilityPolicy policy,
        SandboxExecutionProfile sandbox)
    {
        if (policy.Version == ExecutionCapabilityPolicy.LegacyVersion)
            return;
        if (policy.Version != ExecutionCapabilityPolicy.CurrentVersion ||
            string.IsNullOrWhiteSpace(policy.Authority))
        {
            throw new InvalidOperationException(
                "Docker capabilities are missing a supported version or policy authority.");
        }
        if (policy.FileSystem.Read.Count != 1 || policy.FileSystem.Read[0] != "**")
            throw new InvalidOperationException("Docker capabilities may read only the staged workspace.");
        if (policy.Processes.Any(rule =>
                string.IsNullOrWhiteSpace(rule.Executable) ||
                rule.ArgumentPrefix.Count == 0 ||
                rule.Phases.Count == 0))
        {
            throw new InvalidOperationException("Docker process capabilities are incomplete.");
        }
        if (ParseCpu(sandbox.CpuLimit) > ParseCpu(policy.Resources.CpuLimit) ||
            ParseMemory(sandbox.MemoryLimit) > ParseMemory(policy.Resources.MemoryLimit) ||
            sandbox.ProcessLimit > policy.Resources.ProcessLimit ||
            sandbox.WallClockSeconds > policy.Resources.WallClockSeconds)
        {
            throw new InvalidOperationException(
                "Docker sandbox resources exceed the authoritative capability maxima.");
        }
        if (sandbox.NetworkAccess &&
            (policy.Network.Destinations.Count == 0 || policy.Network.Phases.Count == 0))
        {
            throw new InvalidOperationException(
                "Docker network access lacks explicit destination and phase capabilities.");
        }
    }

    private DockerCapabilityDecision Denied(string phase, string reason) =>
        DockerCapabilityDecision.Denied(phase, reason);

    private List<DockerWritableMount> ResolveWritableMounts()
    {
        var mounts = new Dictionary<string, DockerWritableMount>(PathComparer());
        foreach (var pattern in _policy.FileSystem.Write)
        {
            if (pattern == "**")
                continue;
            if (pattern is "**/bin/**" or "**/obj/**")
            {
                var directoryName = pattern == "**/bin/**" ? "bin" : "obj";
                foreach (var project in EnumerateProjectFilesSafely())
                {
                    AddWritableDirectory(
                        Path.Combine(Path.GetDirectoryName(project)!, directoryName),
                        mounts);
                }
                continue;
            }

            var relative = pattern[..^3].Replace('/', Path.DirectorySeparatorChar);
            AddWritableDirectory(Path.Combine(_workspace, relative), mounts);
        }
        return mounts.Values.OrderBy(mount => mount.ContainerPath, StringComparer.Ordinal).ToList();
    }

    private void AddWritableDirectory(
        string path,
        Dictionary<string, DockerWritableMount> mounts)
    {
        var fullPath = Path.GetFullPath(path);
        EnsureWithinWorkspaceAndNoLinks(fullPath);
        Directory.CreateDirectory(fullPath);
        EnsureTreeContainsNoLinks(fullPath);
        var relative = Path.GetRelativePath(_workspace, fullPath).Replace('\\', '/');
        if (fullPath.Contains(',') || relative.Contains(','))
        {
            throw new InvalidOperationException(
                "Capability write mount contains an unsupported Docker mount delimiter.");
        }
        mounts[fullPath] = new DockerWritableMount(fullPath, $"/workspace/{relative}");
    }

    private IEnumerable<string> EnumerateProjectFilesSafely()
    {
        var pending = new Stack<string>();
        pending.Push(_workspace);
        while (pending.Count > 0)
        {
            var directory = pending.Pop();
            foreach (var child in Directory.EnumerateDirectories(directory))
            {
                var name = Path.GetFileName(child);
                if (name.Equals(".git", StringComparison.OrdinalIgnoreCase) ||
                    (File.GetAttributes(child) & FileAttributes.ReparsePoint) != 0)
                {
                    continue;
                }
                pending.Push(child);
            }
            foreach (var file in Directory.EnumerateFiles(directory))
            {
                if (ProjectExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase))
                    yield return file;
            }
        }
    }

    private void EnsureWithinWorkspaceAndNoLinks(string path)
    {
        var comparer = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!path.StartsWith(_workspace + Path.DirectorySeparatorChar, comparer))
            throw new InvalidOperationException("Capability write mount escapes the staged workspace.");
        var current = _workspace;
        foreach (var segment in Path.GetRelativePath(_workspace, path).Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, segment);
            if ((File.Exists(current) || Directory.Exists(current)) &&
                (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    "Capability write mount crosses a symbolic link or reparse point.");
            }
        }
    }

    private static void EnsureTreeContainsNoLinks(string root)
    {
        var pending = new Stack<string>();
        pending.Push(root);
        while (pending.Count > 0)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(pending.Pop()))
            {
                var attributes = File.GetAttributes(entry);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    throw new InvalidOperationException(
                        "Capability write directory contains a symbolic link or reparse point.");
                }
                if ((attributes & FileAttributes.Directory) != 0)
                    pending.Push(entry);
            }
        }
    }

    private static bool StartsWith(
        IReadOnlyList<string> arguments,
        IReadOnlyList<string> prefix) =>
        arguments.Count >= prefix.Count &&
        prefix.Select((value, index) => string.Equals(
            value,
            arguments[index],
            StringComparison.Ordinal)).All(matches => matches);

    private static StringComparer PathComparer() => OperatingSystem.IsWindows()
        ? StringComparer.OrdinalIgnoreCase
        : StringComparer.Ordinal;

    private static decimal ParseCpu(string value) => decimal.Parse(
        value,
        NumberStyles.AllowDecimalPoint,
        CultureInfo.InvariantCulture);

    private static long ParseMemory(string value)
    {
        var suffix = char.ToLowerInvariant(value[^1]);
        var hasSuffix = suffix is 'k' or 'm' or 'g';
        var amount = long.Parse(hasSuffix ? value[..^1] : value, CultureInfo.InvariantCulture);
        return checked(amount * (suffix switch
        {
            'k' => 1024L,
            'm' => 1024L * 1024,
            'g' => 1024L * 1024 * 1024,
            _ => 1L
        }));
    }
}

internal sealed class DockerCapabilityDecision
{
    public bool IsAllowed { get; init; }
    public string Phase { get; init; } = string.Empty;
    public bool RootWritable { get; init; }
    public string NetworkMode { get; init; } = "none";
    public List<DockerWritableMount> WritableMounts { get; init; } = [];
    public Dictionary<string, string> Secrets { get; init; } = new(StringComparer.Ordinal);
    public List<string> Grants { get; init; } = [];
    public List<string> Denials { get; init; } = [];

    public static DockerCapabilityDecision Allowed(
        string phase,
        bool rootWritable,
        string networkMode,
        List<DockerWritableMount> writableMounts,
        IReadOnlyDictionary<string, string> secrets,
        List<string> grants) => new()
        {
            IsAllowed = true,
            Phase = phase,
            RootWritable = rootWritable,
            NetworkMode = networkMode,
            WritableMounts = writableMounts,
            Secrets = new Dictionary<string, string>(secrets, StringComparer.Ordinal),
            Grants = grants
        };

    public static DockerCapabilityDecision Denied(string phase, string reason) => new()
    {
        Phase = phase,
        Denials = [reason]
    };
}

internal sealed record DockerWritableMount(string HostPath, string ContainerPath);
