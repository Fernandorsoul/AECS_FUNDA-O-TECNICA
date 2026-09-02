using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Staging;

internal sealed class DefaultStagedProcessRunnerFactory : IStagedProcessRunnerFactory
{
    private readonly IProcessRunner _hostProcessRunner;

    public DefaultStagedProcessRunnerFactory(IProcessRunner hostProcessRunner)
    {
        _hostProcessRunner = hostProcessRunner;
    }

    public Task<IProcessRunner> CreateAsync(
        string stagedWorkspacePath,
        RepositoryExecutionProfile profile,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!string.Equals(
                profile.Runtime,
                RepositoryExecutionProfile.HostRuntime,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The Docker sandbox is required for staged commands, but no Docker " +
                "runner factory was configured. Host execution is permitted only when " +
                "execution.runtime is explicitly set to 'host' for development.");
        }

        return Task.FromResult<IProcessRunner>(
            new DevelopmentHostProcessRunner(
                _hostProcessRunner,
                profile.EffectiveCapabilities));
    }
}

internal sealed class DevelopmentHostProcessRunner : IProcessRunner
{
    private readonly IProcessRunner _inner;
    private readonly ExecutionCapabilityPolicy _capabilities;

    public DevelopmentHostProcessRunner(
        IProcessRunner inner,
        ExecutionCapabilityPolicy capabilities)
    {
        _inner = inner;
        _capabilities = capabilities;
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
                ProcessLimit = 0,
                WallClockLimitSeconds = CeilingSeconds(request.Timeout),
                WorkspaceMount = "host-direct",
                DevelopmentHostOverride = true,
                Capabilities = new ExecutionCapabilityEvidence
                {
                    PolicyVersion = _capabilities.Version,
                    Authority = _capabilities.Authority,
                    PolicyHash = ExecutionCapabilityPolicyFingerprint.Create(_capabilities),
                    Phase = request.Phase,
                    Granted = ["host-development-override:unrestricted"]
                }
            }
        };
    }

    private static int CeilingSeconds(TimeSpan value) =>
        Math.Max(1, (int)Math.Ceiling(value.TotalSeconds));
}
