namespace DistributedLookup.Domain.Entities;

/// <summary>
/// Represents the aggregate root for a distributed lookup job.
/// Follows DDD principles - all state changes go through domain methods.
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
    
    private readonly Dictionary<ServiceType, ServiceResult> _results = new();
    public IReadOnlyDictionary<ServiceType, ServiceResult> Results => _results;

    // Private constructor for EF/serialization
    private LookupJob() 
    {
        JobId = string.Empty;
        Target = string.Empty;
    }

    public LookupJob(string jobId, string target, LookupTarget targetType, IEnumerable<ServiceType> services)
    {
        if (string.IsNullOrWhiteSpace(jobId))
            throw new ArgumentException("JobId cannot be empty", nameof(jobId));
        
        if (string.IsNullOrWhiteSpace(target))
            throw new ArgumentException("Target cannot be empty", nameof(target));

        JobId = jobId;
        Target = target;
        TargetType = targetType;
        Status = JobStatus.Pending;
        CreatedAt = DateTime.UtcNow;
        _requestedServices.AddRange(services);
    }

    public void MarkAsProcessing()
    {
        if (Status != JobStatus.Pending)
            throw new InvalidOperationException($"Cannot transition from {Status} to Processing");
        
        Status = JobStatus.Processing;
    }

    public void AddResult(ServiceType serviceType, ServiceResult result)
    {
        if (Status == JobStatus.Completed || Status == JobStatus.Failed)
            throw new InvalidOperationException($"Cannot add results to a {Status} job");

        if (!_requestedServices.Contains(serviceType))
            throw new InvalidOperationException($"Service {serviceType} was not requested for this job");

        _results[serviceType] = result;

        // Check if all services have completed
        if (_results.Count == _requestedServices.Count)
        {
            CompleteJob();
        }
    }

    private void CompleteJob()
    {
        Status = JobStatus.Completed;
        CompletedAt = DateTime.UtcNow;
    }

    public void MarkAsFailed(string reason)
    {
        Status = JobStatus.Failed;
        CompletedAt = DateTime.UtcNow;
        // In a real system, we'd store the failure reason
    }

    public bool IsComplete() => Status == JobStatus.Completed || Status == JobStatus.Failed;

    public int CompletionPercentage()
    {
        if (_requestedServices.Count == 0) return 0;
        return (_results.Count * 100) / _requestedServices.Count;
    }
}
