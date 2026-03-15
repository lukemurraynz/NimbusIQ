using Atlas.ControlPlane.Domain.Entities;
using Atlas.ControlPlane.Infrastructure.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Atlas.ControlPlane.Infrastructure.Persistence.Repositories;

/// <summary>
/// T024: Discovery snapshot persistence implementation
/// </summary>
public class DiscoverySnapshotRepository : IDiscoverySnapshotRepository
{
    private readonly AtlasDbContext _context;
    private readonly ILogger<DiscoverySnapshotRepository> _logger;

    public DiscoverySnapshotRepository(
        AtlasDbContext context,
        ILogger<DiscoverySnapshotRepository> logger)
    {
        _context = context;
        _logger = logger;
    }

    public async Task<DiscoverySnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        return await _context.DiscoverySnapshots
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == id, cancellationToken);
    }

    public async Task<List<DiscoverySnapshot>> GetByAnalysisRunIdAsync(Guid analysisRunId, CancellationToken cancellationToken = default)
    {
        return await _context.DiscoverySnapshots
            .Where(s => s.AnalysisRunId == analysisRunId)
            .AsNoTracking()
            .OrderByDescending(s => s.SnapshotTime)
            .ToListAsync(cancellationToken);
    }

    public async Task<DiscoverySnapshot> CreateAsync(DiscoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        _context.DiscoverySnapshots.Add(snapshot);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Created discovery snapshot {SnapshotId} for analysis {AnalysisRunId}",
            snapshot.Id, snapshot.AnalysisRunId);

        return snapshot;
    }

    public async Task UpdateAsync(DiscoverySnapshot snapshot, CancellationToken cancellationToken = default)
    {
        _context.DiscoverySnapshots.Update(snapshot);
        await _context.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Updated discovery snapshot {SnapshotId}", snapshot.Id);
    }
}
