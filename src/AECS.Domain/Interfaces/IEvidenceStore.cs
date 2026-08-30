using AECS.Domain.Models;

namespace AECS.Domain.Interfaces;

public interface IEvidenceStore
{
    Task AppendAsync(EvidenceEvent evidence, CancellationToken cancellationToken);
    Task<IReadOnlyList<EvidenceEvent>> GetByTaskAsync(string taskId, CancellationToken cancellationToken);
}
