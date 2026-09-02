using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IProactiveAlertStore
{
    void EnsureRepositoryIsolation(string repositoryPath);

    Task<IReadOnlyList<ProactiveAlert>> ListAsync(
        string repositoryPath,
        CancellationToken cancellationToken);

    Task<ProactiveAlert?> LoadAsync(
        string repositoryPath,
        Guid alertId,
        CancellationToken cancellationToken);

    Task SaveAsync(ProactiveAlert alert, CancellationToken cancellationToken);
}
