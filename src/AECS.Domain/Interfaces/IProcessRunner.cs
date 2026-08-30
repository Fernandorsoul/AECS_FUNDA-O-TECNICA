using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IProcessRunner
{
    Task<ProcessExecutionResult> RunAsync(
        ProcessExecutionRequest request,
        CancellationToken cancellationToken);
}
