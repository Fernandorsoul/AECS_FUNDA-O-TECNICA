using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class BuildVerifier : IVerifier
{
    private const int MaxTransientRetries = 2;
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(5);
    private readonly IProcessRunner _processRunner;
    private readonly Func<TimeSpan>? _remainingDuration;

    public BuildVerifier(
        IProcessRunner processRunner,
        Func<TimeSpan>? remainingDuration = null)
    {
        _processRunner = processRunner;
        _remainingDuration = remainingDuration;
    }

    public string Name => "Build";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var timeout = GetTimeout(context.Contract.Budget);
            if (timeout <= TimeSpan.Zero)
                return Error(context.AgentRunId, "Wall-clock budget exhausted before build");
            var execution = RepositoryExecutionProfileResolver.Resolve(
                context.RepoPath,
                context.Contract.Execution);
            var request = new ProcessExecutionRequest
            {
                FileName = "dotnet",
                Arguments = execution.BuildArguments,
                WorkingDirectory = execution.WorkingDirectory,
                Timeout = timeout,
                Phase = $"{context.Phase}.build"
            };

            ProcessExecutionResult? result = null;
            for (var attempt = 0; attempt <= MaxTransientRetries; attempt++)
            {
                result = await _processRunner.RunAsync(request, cancellationToken);
                context.CommandEvidence.Add(
                    ExecutionCommandEvidenceFactory.Create(context, request, result));

                if (result.TimedOut || result.Cancelled)
                {
                    return Error(
                        context.AgentRunId,
                        result.TimedOut
                            ? "Build timed out; process tree terminated"
                            : "Build cancelled; process tree terminated");
                }

                if (result.ExitCode == 0 || !IsTransientError(result) || attempt == MaxTransientRetries)
                    break;

                await Task.Delay(RetryDelay, cancellationToken);
            }

            var output = JoinOutput(result!);
            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = result!.ExitCode == 0 ? VerificationStatus.Pass : VerificationStatus.Fail,
                Severity = result.ExitCode == 0 ? Severity.Info : Severity.Error,
                Message = result.ExitCode == 0 ? "Build succeeded" : $"Build failed: {output}"
            };
        }
        catch (Exception ex)
        {
            return Error(context.AgentRunId, $"Build verifier error: {ex.Message}");
        }
    }

    private static bool IsTransientError(ProcessExecutionResult result)
    {
        if (result.ExitCode == 0)
            return false;
        var output = JoinOutput(result);
        return output.Contains("NU1301", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("NU1302", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("NU1303", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Unable to load the service index", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Resource temporarily unavailable", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("Connection refused", StringComparison.OrdinalIgnoreCase) ||
            output.Contains("No space left on device", StringComparison.OrdinalIgnoreCase);
    }

    private VerificationResult Error(string agentRunId, string message) => new()
    {
        AgentRunId = agentRunId,
        Verifier = Name,
        Status = VerificationStatus.Error,
        Severity = Severity.Critical,
        Message = message
    };

    private static string JoinOutput(ProcessExecutionResult result) =>
        string.Join(Environment.NewLine, new[] { result.StandardOutput, result.StandardError }
            .Where(value => !string.IsNullOrWhiteSpace(value)))
            .Trim();

    private TimeSpan GetTimeout(ExecutionBudget budget)
    {
        var configured = TimeSpan.FromSeconds(Math.Max(1, budget.MaxDurationSeconds));
        if (_remainingDuration is null)
            return configured;
        var remaining = _remainingDuration();
        return remaining < configured ? remaining : configured;
    }
}
