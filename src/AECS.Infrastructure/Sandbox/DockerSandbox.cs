using System.Globalization;
using System.Text.RegularExpressions;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Infrastructure.Sandbox;

public sealed partial class DockerStagedProcessRunnerFactory : IStagedProcessRunnerFactory
{
    private static readonly TimeSpan DockerProbeTimeout = TimeSpan.FromSeconds(15);
    private readonly IProcessRunner _hostProcessRunner;
    private readonly bool _allowHostExecution;

    public DockerStagedProcessRunnerFactory(
        IProcessRunner hostProcessRunner,
        bool allowHostExecution = false)
    {
        _hostProcessRunner = hostProcessRunner;
        _allowHostExecution = allowHostExecution;
    }

    public async Task<IProcessRunner> CreateAsync(
        string stagedWorkspacePath,
        RepositoryExecutionProfile profile,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(profile);
        var workspace = ValidateStagedWorkspace(stagedWorkspacePath);
        var runtime = profile.EffectiveRuntime;

        if (runtime == RepositoryExecutionProfile.HostRuntime)
        {
            if (!_allowHostExecution)
            {
                throw new InvalidOperationException(
                    "The task requests host execution, but the development override is disabled. " +
                    "Use --allow-host-execution explicitly only in a trusted development environment.");
            }
            if (!string.Equals(
                    profile.Runtime,
                    RepositoryExecutionProfile.HostRuntime,
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "Host execution must be explicitly selected with execution.runtime: host.");
            }

            return new HostDevelopmentProcessRunner(_hostProcessRunner);
        }

        if (runtime != RepositoryExecutionProfile.DockerRuntime)
        {
            throw new InvalidOperationException(
                $"Unsupported staged execution runtime: '{profile.Runtime}'.");
        }

        var sandbox = profile.Sandbox ?? new SandboxExecutionProfile();
        ValidateSandbox(sandbox);

        ProcessExecutionResult probe;
        try
        {
            probe = await _hostProcessRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "docker",
                Arguments = ["version", "--format", "{{.Server.Version}}"],
                WorkingDirectory = workspace,
                Timeout = DockerProbeTimeout
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            throw DockerUnavailable(ex.Message, ex);
        }

        if (probe.Cancelled && cancellationToken.IsCancellationRequested)
            cancellationToken.ThrowIfCancellationRequested();
        if (!probe.Succeeded || string.IsNullOrWhiteSpace(probe.StandardOutput))
        {
            throw DockerUnavailable(
                string.Join(' ', new[] { probe.StandardError, probe.StandardOutput }
                    .Where(value => !string.IsNullOrWhiteSpace(value))).Trim());
        }

        var containerUser = await ResolveContainerUserAsync(workspace, cancellationToken);
        return new DockerSandboxProcessRunner(
            _hostProcessRunner,
            workspace,
            sandbox,
            probe.StandardOutput.Trim(),
            containerUser);
    }

    private async Task<string?> ResolveContainerUserAsync(
        string workspace,
        CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
            return null;

        var uid = await RunIdentityProbeAsync("-u", workspace, cancellationToken);
        var gid = await RunIdentityProbeAsync("-g", workspace, cancellationToken);
        return uid is null || gid is null ? null : $"{uid}:{gid}";
    }

    private async Task<string?> RunIdentityProbeAsync(
        string argument,
        string workspace,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _hostProcessRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "id",
                Arguments = [argument],
                WorkingDirectory = workspace,
                Timeout = TimeSpan.FromSeconds(5)
            }, cancellationToken);
            var value = result.StandardOutput.Trim();
            return result.Succeeded && value.All(char.IsDigit) ? value : null;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return null;
        }
    }

    private static string ValidateStagedWorkspace(string stagedWorkspacePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stagedWorkspacePath);
        var workspace = Path.GetFullPath(stagedWorkspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (!Directory.Exists(workspace))
            throw new DirectoryNotFoundException($"Staged workspace not found: {workspace}");
        if (!Path.GetFileName(workspace).StartsWith("aecs-staging-", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "Docker execution accepts only an AECS disposable staged workspace; " +
                "the original checkout cannot be mounted.");
        }
        if (workspace.Contains(','))
        {
            throw new InvalidOperationException(
                "The staged workspace path cannot contain ',' because Docker --mount " +
                "cannot represent it without ambiguity.");
        }

        return workspace;
    }

    internal static void ValidateSandbox(SandboxExecutionProfile sandbox)
    {
        if (!PinnedImageRegex().IsMatch(sandbox.Image))
        {
            throw new InvalidOperationException(
                "Docker sandbox image must be an immutable reference pinned by sha256 digest.");
        }
        if (!decimal.TryParse(
                sandbox.CpuLimit,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var cpu) || cpu <= 0)
        {
            throw new InvalidOperationException("Docker sandbox CPU limit must be positive.");
        }
        if (!MemoryLimitRegex().IsMatch(sandbox.MemoryLimit))
            throw new InvalidOperationException("Docker sandbox memory limit is invalid.");
        if (sandbox.ProcessLimit <= 0)
            throw new InvalidOperationException("Docker sandbox process limit must be positive.");
        if (sandbox.WallClockSeconds <= 0)
            throw new InvalidOperationException("Docker sandbox wall-clock limit must be positive.");
    }

    private static InvalidOperationException DockerUnavailable(
        string detail,
        Exception? inner = null) => new(
        "The Docker sandbox is required, but the Docker daemon is unavailable. " +
        "Execution failed closed before repository code was run. " + detail,
        inner);

    [GeneratedRegex(@"^[^\s@]+@sha256:[a-fA-F0-9]{64}$", RegexOptions.CultureInvariant)]
    private static partial Regex PinnedImageRegex();

    [GeneratedRegex(@"^[1-9][0-9]*(?:[kKmMgG])?$", RegexOptions.CultureInvariant)]
    private static partial Regex MemoryLimitRegex();
}

public sealed class DockerSandboxProcessRunner : IProcessRunner
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private readonly IProcessRunner _dockerCliRunner;
    private readonly string _workspace;
    private readonly SandboxExecutionProfile _sandbox;
    private readonly string _dockerVersion;
    private readonly string? _containerUser;

    public DockerSandboxProcessRunner(
        IProcessRunner dockerCliRunner,
        string stagedWorkspacePath,
        SandboxExecutionProfile sandbox,
        string dockerVersion,
        string? containerUser = null)
    {
        _dockerCliRunner = dockerCliRunner;
        _workspace = Path.GetFullPath(stagedWorkspacePath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        _sandbox = sandbox;
        _dockerVersion = dockerVersion;
        _containerUser = containerUser;
        DockerStagedProcessRunnerFactory.ValidateSandbox(sandbox);
    }

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(request.FileName);
        var containerWorkingDirectory = MapWorkingDirectory(request.WorkingDirectory);
        var timeout = request.Timeout < TimeSpan.FromSeconds(_sandbox.WallClockSeconds)
            ? request.Timeout
            : TimeSpan.FromSeconds(_sandbox.WallClockSeconds);
        if (timeout <= TimeSpan.Zero)
            throw new InvalidOperationException("Sandbox command timeout must be positive.");

        var containerName = $"aecs-sandbox-{Guid.NewGuid():N}";
        var arguments = BuildDockerArguments(
            request,
            containerName,
            containerWorkingDirectory);
        ProcessExecutionResult result;
        try
        {
            result = await _dockerCliRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "docker",
                Arguments = arguments,
                WorkingDirectory = _workspace,
                Timeout = timeout
            }, cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            result = new ProcessExecutionResult
            {
                ExitCode = -1,
                StandardError = $"Docker sandbox execution failed closed: {ex.Message}"
            };
        }
        finally
        {
            await RemoveContainerAsync(containerName);
        }

        return CopyWithEnvironment(result, timeout);
    }

    private IReadOnlyList<string> BuildDockerArguments(
        ProcessExecutionRequest request,
        string containerName,
        string containerWorkingDirectory)
    {
        var arguments = new List<string>
        {
            "run",
            "--rm",
            "--pull=missing",
            "--name", containerName,
            "--label", "aecs.sandbox=true",
            "--init",
            "--read-only",
            "--cap-drop", "ALL",
            "--security-opt", "no-new-privileges",
            "--network", _sandbox.NetworkAccess ? "bridge" : "none",
            "--cpus", _sandbox.CpuLimit,
            "--memory", _sandbox.MemoryLimit,
            "--pids-limit", _sandbox.ProcessLimit.ToString(CultureInfo.InvariantCulture),
            "--tmpfs", "/tmp:rw,nosuid,nodev,noexec,size=64m,mode=1777",
            "--mount", $"type=bind,source={_workspace},target=/workspace",
            "--workdir", containerWorkingDirectory,
            "--env", "HOME=/tmp/aecs",
            "--env", "DOTNET_CLI_HOME=/tmp/aecs",
            "--env", "NUGET_PACKAGES=/tmp/nuget",
            "--env", "DOTNET_NOLOGO=1",
            "--env", "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
        };

        if (!string.IsNullOrWhiteSpace(_containerUser))
        {
            arguments.Add("--user");
            arguments.Add(_containerUser);
        }

        arguments.Add(_sandbox.Image);
        arguments.Add(MapWorkspacePath(request.FileName));
        arguments.AddRange(request.Arguments.Select(MapWorkspacePath));
        return arguments;
    }

    private string MapWorkingDirectory(string workingDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(workingDirectory);
        var hostDirectory = Path.GetFullPath(workingDirectory);
        if (!Directory.Exists(hostDirectory))
            throw new DirectoryNotFoundException($"Command working directory not found: {hostDirectory}");
        EnsureWithinWorkspace(hostDirectory, "working directory");

        var relative = Path.GetRelativePath(_workspace, hostDirectory).Replace('\\', '/');
        return relative == "." ? "/workspace" : $"/workspace/{relative}";
    }

    private string MapWorkspacePath(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return value;

        try
        {
            if (!Path.IsPathRooted(value))
                return value;
            var fullPath = Path.GetFullPath(value);
            if (!IsWithinWorkspace(fullPath))
                return value;
            var relative = Path.GetRelativePath(_workspace, fullPath).Replace('\\', '/');
            return relative == "." ? "/workspace" : $"/workspace/{relative}";
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException)
        {
            return value;
        }
    }

    private void EnsureWithinWorkspace(string path, string description)
    {
        if (!IsWithinWorkspace(path))
        {
            throw new InvalidOperationException(
                $"Sandbox command {description} escapes the staged workspace: {path}");
        }

        var relative = Path.GetRelativePath(_workspace, path);
        var current = _workspace;
        foreach (var segment in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".")
                continue;
            current = Path.Combine(current, segment);
            if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
            {
                throw new InvalidOperationException(
                    $"Sandbox command {description} crosses a symbolic link or junction: {path}");
            }
        }
    }

    private bool IsWithinWorkspace(string path)
    {
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        return string.Equals(path, _workspace, comparison) ||
            path.StartsWith(_workspace + Path.DirectorySeparatorChar, comparison);
    }

    private async Task RemoveContainerAsync(string containerName)
    {
        try
        {
            await _dockerCliRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "docker",
                Arguments = ["rm", "--force", containerName],
                WorkingDirectory = _workspace,
                Timeout = CleanupTimeout
            }, CancellationToken.None);
        }
        catch
        {
            // The run command also uses --rm. This second cleanup is deliberately
            // best effort for cases where cancellation killed the Docker CLI first.
        }
    }

    private ProcessExecutionResult CopyWithEnvironment(
        ProcessExecutionResult result,
        TimeSpan timeout)
    {
        var digestSeparator = _sandbox.Image.LastIndexOf('@');
        return new ProcessExecutionResult
        {
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            Duration = result.Duration,
            TimedOut = result.TimedOut,
            Cancelled = result.Cancelled,
            Environment = new ExecutionEnvironmentEvidence
            {
                Runtime = RepositoryExecutionProfile.DockerRuntime,
                RuntimeVersion = _dockerVersion,
                Image = _sandbox.Image[..digestSeparator],
                ImageDigest = _sandbox.Image[(digestSeparator + 1)..].ToLowerInvariant(),
                NetworkMode = _sandbox.NetworkAccess ? "bridge" : "none",
                CpuLimit = _sandbox.CpuLimit,
                MemoryLimit = _sandbox.MemoryLimit,
                ProcessLimit = _sandbox.ProcessLimit,
                WallClockLimitSeconds = Math.Max(1, (int)Math.Ceiling(timeout.TotalSeconds)),
                WorkspaceMount = "/workspace:rw",
                DevelopmentHostOverride = false
            }
        };
    }
}

internal sealed class HostDevelopmentProcessRunner : IProcessRunner
{
    private readonly IProcessRunner _inner;

    public HostDevelopmentProcessRunner(IProcessRunner inner)
    {
        _inner = inner;
    }

    public async Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken)
    {
        var result = await _inner.RunAsync(request, cancellationToken);
        return new ProcessExecutionResult
        {
            ExitCode = result.ExitCode,
            StandardOutput = result.StandardOutput,
            StandardError = result.StandardError,
            Duration = result.Duration,
            TimedOut = result.TimedOut,
            Cancelled = result.Cancelled,
            Environment = new ExecutionEnvironmentEvidence
            {
                Runtime = RepositoryExecutionProfile.HostRuntime,
                RuntimeVersion = Environment.OSVersion.VersionString,
                NetworkMode = "host",
                CpuLimit = "unlimited",
                MemoryLimit = "unlimited",
                WallClockLimitSeconds = Math.Max(
                    1,
                    (int)Math.Ceiling(request.Timeout.TotalSeconds)),
                WorkspaceMount = "host-direct",
                DevelopmentHostOverride = true
            }
        };
    }
}
