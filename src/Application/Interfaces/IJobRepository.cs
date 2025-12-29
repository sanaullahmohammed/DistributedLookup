using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Application.Interfaces;

/// <summary>
/// Repository interface for LookupJob persistence.
/// Following repository pattern to abstract data access.
/// </summary>
public interface IJobRepository
{
    Task<LookupJob?> GetByIdAsync(string jobId, CancellationToken cancellationToken = default);
    Task SaveAsync(LookupJob job, CancellationToken cancellationToken = default);
    Task<IEnumerable<LookupJob>> GetPendingJobsAsync(CancellationToken cancellationToken = default);
}
