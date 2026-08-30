using AECS.Domain.Interfaces;
using AECS.Domain.Models;
using AECS.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace AECS.Infrastructure.Repositories;

public class EvidenceStore : IEvidenceStore
{
    private readonly AecsDbContext _db;

    public EvidenceStore(AecsDbContext db)
    {
        _db = db;
    }

    public async Task AppendAsync(EvidenceEvent evidence, CancellationToken cancellationToken)
    {
        _db.EvidenceEvents.Add(evidence);
        await _db.SaveChangesAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<EvidenceEvent>> GetByTaskAsync(string taskId, CancellationToken cancellationToken)
    {
        return await _db.EvidenceEvents
            .Where(e => e.TaskId == taskId)
            .OrderBy(e => e.OccurredAt)
            .ToListAsync(cancellationToken);
    }
}
