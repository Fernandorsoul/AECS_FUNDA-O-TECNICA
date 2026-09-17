using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

/// <summary>
/// Durable storage for cross-turn constraint ledger state (plan §4.3).
/// Only persistable-origin records and conflicts are stored; contract-projected
/// constraints are re-derived from the sealed TaskContract on every run.
/// </summary>
public interface IConstraintLedgerStore
{
    Task<ConstraintLedgerSnapshot?> LoadAsync(
        string repositoryPath,
        CancellationToken cancellationToken);

    Task SaveAsync(
        string repositoryPath,
        ConstraintLedgerSnapshot snapshot,
        CancellationToken cancellationToken);
}
