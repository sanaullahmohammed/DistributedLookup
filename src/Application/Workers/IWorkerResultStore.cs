using System.Text.Json;
using DistributedLookup.Contracts;
using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Application.Workers;

/// <summary>
/// Abstraction for worker result storage (write-only).
/// Workers use this to store results independently from saga state.
/// Each storage backend (Redis, S3, DynamoDB, etc.) implements this interface.
/// 
/// NOTE: This is write-only. The API uses IWorkerResultReader to read results.
/// </summary>
public interface IWorkerResultStore
{
    /// <summary>
    /// The storage type this store handles.
    /// </summary>
    StorageType StorageType { get; }
    
    /// <summary>
    /// Saves a successful lookup result.
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="serviceType">The service type that produced this result</param>
    /// <param name="data">The lookup result data</param>
    /// <param name="duration">How long the lookup took</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Location metadata for where the result was stored</returns>
    Task<ResultLocation> SaveResultAsync(
        string jobId,
        ServiceType serviceType,
        JsonDocument data,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
    
    /// <summary>
    /// Saves a failed lookup result.
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="serviceType">The service type that failed</param>
    /// <param name="errorMessage">The error message</param>
    /// <param name="duration">How long before the failure occurred</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>Location metadata for where the failure was stored</returns>
    Task<ResultLocation> SaveFailureAsync(
        string jobId,
        ServiceType serviceType,
        string errorMessage,
        TimeSpan duration,
        CancellationToken cancellationToken = default);
}
