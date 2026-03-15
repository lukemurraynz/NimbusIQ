using Atlas.ControlPlane.Domain.Entities;

namespace Atlas.ControlPlane.Infrastructure.Persistence.Repositories;

/// <summary>
/// T024: Discovery persistence repository interface
/// </summary>
public interface IDiscoverySnapshotRepository
{
    Task<DiscoverySnapshot?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default);
    Task<List<DiscoverySnapshot>> GetByAnalysisRunIdAsync(Guid analysisRunId, CancellationToken cancellationToken = default);
    Task<DiscoverySnapshot> CreateAsync(DiscoverySnapshot snapshot, CancellationToken cancellationToken = default);
    Task UpdateAsync(DiscoverySnapshot snapshot, CancellationToken cancellationToken = default);
}
