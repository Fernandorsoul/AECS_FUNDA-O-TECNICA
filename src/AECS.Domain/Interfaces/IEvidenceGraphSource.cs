using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IEvidenceGraphSource
{
    Task<EvidenceGraphQueryResult> QueryEvidenceGraphsAsync(
        EvidenceGraphQuery query,
        EvidenceReadScope scope,
        CancellationToken cancellationToken);

    Task<EvidenceGraph?> LoadEvidenceGraphAsync(
        Guid evidenceId,
        EvidenceReadScope scope,
        CancellationToken cancellationToken);
}
