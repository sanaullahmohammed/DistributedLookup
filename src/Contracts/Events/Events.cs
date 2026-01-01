using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Contracts.Events;

/// <summary>
/// Event published when a new lookup job is submitted
/// </summary>
public record JobSubmitted
{
    public string JobId { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public LookupTarget TargetType { get; init; }
    public ServiceType[] Services { get; init; } = Array.Empty<ServiceType>();
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}

/// <summary>
/// Event published when a task completes (success or failure).
/// Workers publish this after storing results in their independent storage.
/// Contains ResultLocation metadata (WHERE result is stored), NOT the actual data.
/// </summary>
public record TaskCompleted
{
    public string JobId { get; init; } = string.Empty;
    public ServiceType ServiceType { get; init; }
    public bool Success { get; init; }
    public string? ErrorMessage { get; init; }
    public TimeSpan Duration { get; init; }
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
    
    /// <summary>
    /// Location where the worker stored the result data.
    /// Saga stores this metadata to enable API result retrieval.
    /// </summary>
    public ResultLocation? ResultLocation { get; init; }
}

/// <summary>
/// Event published when a job fails
/// </summary>
public record JobFailed
{
    public string JobId { get; init; } = string.Empty;
    public string Reason { get; init; } = string.Empty;
    public DateTime Timestamp { get; init; } = DateTime.UtcNow;
}
