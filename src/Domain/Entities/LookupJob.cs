namespace DistributedLookup.Domain.Entities;

/// <summary>
/// Represents the aggregate root for a distributed lookup job.
/// Follows DDD principles - all state changes go through domain methods.
/// 
/// NOTE: This entity does NOT store results. Results are stored independently
/// by workers via IWorkerResultStore. This entity only tracks job metadata.
/// </summary>
public class LookupJob
{
    public string JobId { get; private set; }
    public string Target { get; private set; }
    public LookupTarget TargetType { get; private set; }
    public JobStatus Status { get; private set; }
    public DateTime CreatedAt { get; private set; }
    public DateTime? CompletedAt { get; private set; }
    
    private readonly List<ServiceType> _requestedServices = new();
    public IReadOnlyList<ServiceType> RequestedServices => _requestedServices.AsReadOnly();

    public LookupJob(string jobId, string target, LookupTarget targetType, IEnumerable<ServiceType> services)
        : this(jobId, target, targetType, services, DateTime.UtcNow)
    {
    }

    /// <summary>
    /// Constructor with explicit createdAt timestamp for factory pattern.
    /// Ensures consistent timestamps between job and saga.
    /// </summary>
    public LookupJob(string jobId, string target, LookupTarget targetType, IEnumerable<ServiceType> services, DateTime createdAt)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("JobId cannot be empty", nameof(jobId));
        
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Target cannot be empty", nameof(target));

        JobId = jobId;
        Target = target;
        TargetType = targetType;
        Status = JobStatus.Pending;
        CreatedAt = createdAt;
        _requestedServices.AddRange(services);
    }

    /// <summary>
    /// Internal constructor for reconstituting from storage.
    /// Bypasses validation since data is already validated.
    /// </summary>
    internal LookupJob(
        string jobId,
        string target,
        LookupTarget targetType,
        IEnumerable<ServiceType> services,
        DateTime createdAt,
        JobStatus status,
        DateTime? completedAt)
    {
        JobId = jobId;
        Target = target;
        TargetType = targetType;
        CreatedAt = createdAt;
        Status = status;
        CompletedAt = completedAt;
        _requestedServices.AddRange(services);
    }

    public void MarkAsProcessing()
    {
        if (Status != JobStatus.Pending)
            throw new InvalidOperationException($"Cannot transition from {Status} to Processing");
        
        Status = JobStatus.Processing;
    }

    public void MarkAsCompleted()
    {
        Status = JobStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed(string reason)
    {
        Status = JobStatus.Failed;
        CompletedAt = DateTime.UtcNow;
    }

    public bool IsComplete() => Status == JobStatus.Completed || Status == JobStatus.Failed;
}
