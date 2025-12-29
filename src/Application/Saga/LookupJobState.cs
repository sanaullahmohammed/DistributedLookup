using DistributedLookup.Domain.Entities;
using MassTransit;

namespace DistributedLookup.Application.Saga;

/// <summary>
/// State machine instance data for a lookup job.
/// MassTransit uses this to track saga state in Redis.
/// </summary>
public class LookupJobState : SagaStateMachineInstance, ISagaVersion
{
    public Guid CorrelationId { get; set; }
    public string? CurrentState { get; set; }
    
    public string JobId { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public LookupTarget TargetType { get; set; }
    
    public List<ServiceType> PendingServices { get; set; } = new();
    public List<ServiceType> CompletedServices { get; set; } = new();
    
    public DateTime CreatedAt { get; set; }
    public DateTime? CompletedAt { get; set; }

    public int Version { get; set; }
}
