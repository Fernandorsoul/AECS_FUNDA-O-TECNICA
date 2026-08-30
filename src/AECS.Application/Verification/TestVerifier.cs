using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public class TestVerifier : IVerifier
{
    private readonly IProcessRunner _processRunner;

    public TestVerifier(IProcessRunner processRunner)
    {
        _processRunner = processRunner;
    }

    public string Name => "Tests";
    public VerificationCategory Category => VerificationCategory.HighConfidence;

    public async Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        try
        {
            var execution = RepositoryExecutionProfileResolver.Resolve(
                context.RepoPath,
                context.Contract.Execution);
            var result = await _processRunner.RunAsync(new ProcessExecutionRequest
            {
                FileName = "dotnet",
                Arguments = execution.TestArguments,
                WorkingDirectory = execution.WorkingDirectory,
                Timeout = TimeSpan.FromSeconds(
                    Math.Max(1, context.Contract.Budget.MaxDurationSeconds))
            }, cancellationToken);

            if (result.TimedOut || result.Cancelled)
            {
                return Error(
                    context.AgentRunId,
                    result.TimedOut
                        ? "Tests timed out; process tree terminated"
                        : "Tests cancelled; process tree terminated");
            }

            var output = string.Join(Environment.NewLine, new[]
            {
                result.StandardOutput,
                result.StandardError
            }.Where(value => !string.IsNullOrWhiteSpace(value))).Trim();

            return new VerificationResult
            {
                AgentRunId = context.AgentRunId,
                Verifier = Name,
                Status = result.ExitCode == 0 ? VerificationStatus.Pass : VerificationStatus.Fail,
                Severity = result.ExitCode == 0 ? Severity.Info : Severity.Error,
                Message = result.ExitCode == 0 ? "All tests passed" : $"Tests failed: {output}"
            };
        }
        catch (Exception ex)
        {
            return Error(context.AgentRunId, $"Test verifier error: {ex.Message}");
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
}
