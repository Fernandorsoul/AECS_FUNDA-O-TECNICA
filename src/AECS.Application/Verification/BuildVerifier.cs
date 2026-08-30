using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class BuildVerifier : IVerifier
{
    private readonly IProcessRunner _processRunner;

    public BuildVerifier(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string Name => "Build";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await _processRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "dotnet",
                Arguments = ["build"],
                WorkingDirectory = context.RepoPath,
                Timeout = TimeSpan.FromSeconds(
                    Math.Max(1, context.Contract.Budget.MaxDurationSeconds))
            }, cancellationToken);

            if (result.TimedOut || result.Cancelled)
            {
                return Error(
                    context.AgentRunId,
                    result.TimedOut
                        ? "Build timed out; process tree terminated"
                        : "Build cancelled; process tree terminated");
            }

            var output = JoinOutput(result);
            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = result.ExitCode == 0 ? VerificationStatus.Pass : VerificationStatus.Fail,
                Severity = result.ExitCode == 0 ? Severity.Info : Severity.Error,
                Message = result.ExitCode == 0 ? "Build succeeded" : $"Build failed: {output}"
            };
        }
        catch (Exception ex)
        {
            return Error(context.AgentRunId, $"Build verifier error: {ex.Message}");
        }
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
}
