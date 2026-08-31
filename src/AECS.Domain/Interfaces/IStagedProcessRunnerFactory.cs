using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IStagedProcessRunnerFactory
{
    Task<IProcessRunner> CreateAsync(
        string stagedWorkspacePath,
        RepositoryExecutionProfile profile,
        CancellationToken cancellationToken);
}
