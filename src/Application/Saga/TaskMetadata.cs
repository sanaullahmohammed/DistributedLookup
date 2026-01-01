using DistributedLookup.Contracts;

namespace DistributedLookup.Application.Saga;

/// <summary>
/// Metadata about a completed task stored in saga state.
/// Contains orchestration info + result location (NOT actual data).
/// </summary>
public class TaskMetadata
{
    /// <summary>
    /// Whether the task completed successfully.
    /// </summary>
    public bool Success { get; set; }
    
    /// <summary>
    /// How long the task took to complete.
    /// </summary>
    public TimeSpan Duration { get; set; }
    
    /// <summary>
    /// When the task completed.
    /// </summary>
    public DateTime CompletedAt { get; set; }
    
    /// <summary>
    /// Error message if the task failed.
    /// </summary>
    public string? ErrorMessage { get; set; }
    
    /// <summary>
    /// Where the worker stored the result data.
    /// Used by API to fetch actual result from worker storage.
    /// </summary>
    public ResultLocation? Location { get; set; }
}
