using DistributedLookup.Domain.Entities;
using MassTransit;

namespace DistributedLookup.Application.Saga;

/// <summary>
/// State machine instance data for a lookup job.
/// MassTransit uses this to track saga state in Redis.
/// 
/// Key design decisions:
/// - JobId = CorrelationId (enables O(1) saga lookup)
/// - TaskResults stores metadata + result locations (NOT actual result data)
/// - Actual results are stored independently by workers via IWorkerResultStore
/// - CompletedAt is set ONLY when ALL tasks complete
/// </summary>
public class LookupJobState : SagaStateMachineInstance, ISagaVersion
{
    public Guid CorrelationId { get; set; }
    public string? CurrentState { get; set; }
    
    public string JobId { get; set; } = string.Empty;
    
    // Orchestration state - tracks which services are pending/completed
    public List<ServiceType> PendingServices { get; set; } = new();
    public List<ServiceType> CompletedServices { get; set; } = new();
    
    /// <summary>
    /// Task completion metadata including result locations.
    /// Key is (int)ServiceType for JSON serialization compatibility.
    /// Contains WHERE results are stored (ResultLocation), NOT the actual data.
    /// </summary>
    public Dictionary<int, TaskMetadata> TaskResults { get; set; } = new();
    
    public DateTime CreatedAt { get; set; }
    
    /// <summary>
    /// Set ONLY when ALL tasks complete.
    /// </summary>
    public DateTime? CompletedAt { get; set; }

    public int Version { get; set; }
}
