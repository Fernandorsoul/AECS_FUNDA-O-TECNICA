using AECS.Domain.Enums;
using AECS.Domain.Interfaces;
using AECS.Domain.Models;

namespace AECS.Application.Verification;

public sealed class AgentSuccessVerifier : IVerifier
{
    public string Name => "AgentSuccess";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(new VerificationResult
        {
            AgentRunId = context.AgentRunId,
            Verifier = Name,
            Status = context.AgentResult.Success ? VerificationStatus.Pass : VerificationStatus.Fail,
            Severity = context.AgentResult.Success ? Severity.Info : Severity.Error,
            Message = context.AgentResult.Success
                ? "Agent execution succeeded"
                : $"Agent execution failed: {context.AgentResult.ExitReason} {context.AgentResult.StdErr}".Trim()
        });
}

public sealed class CandidateChangeVerifier : IVerifier
{
    public string Name => "NonEmptyChange";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken)
    {
        var hasChanges = context.CandidateChangeSet.HasChanges;
        return Task.FromResult(new VerificationResult
        {
            AgentRunId = context.AgentRunId,
            Verifier = Name,
            Status = hasChanges ? VerificationStatus.Pass : VerificationStatus.Fail,
            Severity = hasChanges ? Severity.Info : Severity.Error,
            Message = hasChanges
                ? $"Candidate contains {context.CandidateChangeSet.ChangedFiles.Count} changed file(s)"
                : "Candidate contains no filesystem-derived Git changes"
        });
    }
}

public sealed class FileApplicationVerifier : IVerifier
{
    private readonly FileApplicatorResult _applicationResult;

    public FileApplicationVerifier(FileApplicatorResult applicationResult)
    {
        _applicationResult = applicationResult;
    }

    public string Name => "Application";
    public VerificationCategory Category => VerificationCategory.Deterministic;

    public Task<VerificationResult> VerifyAsync(
        VerificationContext context,
        CancellationToken cancellationToken) =>
        Task.FromResult(new VerificationResult
        {
            AgentRunId = context.AgentRunId,
            Verifier = Name,
            Status = _applicationResult.Success ? VerificationStatus.Pass : VerificationStatus.Fail,
            Severity = _applicationResult.Success ? Severity.Info : Severity.Critical,
            Message = _applicationResult.Success
                ? $"Response application accepted ({_applicationResult.AppliedChanges.Count} declarative change(s))"
                : string.Join("; ", _applicationResult.Errors)
        });
}
