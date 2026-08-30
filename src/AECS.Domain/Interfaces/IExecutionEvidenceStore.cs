using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IExecutionEvidenceStore
{
    void EnsureRepositoryIsolation(string repositoryPath);
    Task<string> SaveAsync(ExecutionEvidence evidence, CancellationToken cancellationToken);
    Task<ExecutionEvidence?> LoadAsync(Guid evidenceId, CancellationToken cancellationToken);
}
