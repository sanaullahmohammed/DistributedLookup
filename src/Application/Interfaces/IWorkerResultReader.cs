using System.Text.Json;
using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Application.Interfaces;

/// <summary>
/// Read-only interface for the API to fetch worker results.
/// This is separate from IWorkerResultStore which is used by workers to write results.
/// The API only needs to read results, not write them.
/// </summary>
public interface IWorkerResultReader
{
    /// <summary>
    /// Gets a worker result by job ID and service type.
    /// </summary>
    /// <param name="jobId">The job ID</param>
    /// <param name="serviceType">The service type</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The worker result, or null if not found</returns>
    Task<WorkerResultData?> GetResultAsync(string jobId, ServiceType serviceType, CancellationToken cancellationToken = default);
}

/// <summary>
/// Worker result data returned by the reader.
/// </summary>
public record WorkerResultData
{
    public string JobId { get; init; } = string.Empty;
    public ServiceType ServiceType { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime CompletedAt { get; init; }
    public JsonDocument? Data { get; init; }
}
