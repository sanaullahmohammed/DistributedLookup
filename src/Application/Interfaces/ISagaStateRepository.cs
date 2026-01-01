using DistributedLookup.Application.Saga;

namespace DistributedLookup.Application.Interfaces;

/// <summary>
/// Repository interface for querying saga state.
/// Enables the API to fetch saga state directly by JobId for result aggregation.
/// Key pattern: saga:{jobId} (where JobId = CorrelationId for O(1) lookup)
/// </summary>
public interface ISagaStateRepository
{
    /// <summary>
    /// Gets the saga state for a job by its JobId.
    /// </summary>
    /// <param name="jobId">The job ID (same as saga CorrelationId)</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The saga state, or null if not found</returns>
    Task<LookupJobState?> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default);
}
