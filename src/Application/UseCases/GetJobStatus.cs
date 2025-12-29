using DistributedLookup.Application.Interfaces;
using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Application.UseCases;

/// <summary>
/// Use case for retrieving job status.
/// Follows CQRS pattern - this is a query handler.
/// </summary>
public class GetJobStatus
{
    private readonly IJobRepository _repository;

    public GetJobStatus(IJobRepository repository)
    {
        _repository = repository;
    }

    public async Task<Response?> ExecuteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = await _repository.GetByIdAsync(jobId, cancellationToken);
        
        if (job == null)
        {
            return null;
        }

        return new Response
        {
            JobId = job.JobId,
            Target = job.Target,
            TargetType = job.TargetType,
            Status = job.Status,
            CreatedAt = job.CreatedAt,
            CompletedAt = job.CompletedAt,
            CompletionPercentage = job.CompletionPercentage(),
            RequestedServices = job.RequestedServices.ToList(),
            Results = job.Results.Values.Select(r => new ServiceResultDto
            {
                ServiceType = r.ServiceType,
                Success = r.Success,
                Data = r.Data?.RootElement.ToString(),
                ErrorMessage = r.ErrorMessage,
                CompletedAt = r.CompletedAt,
                DurationMs = (int)r.Duration.TotalMilliseconds
            }).ToList()
        };
    }

    public record Response
    {
        public string JobId { get; init; } = string.Empty;
        public string Target { get; init; } = string.Empty;
        public LookupTarget TargetType { get; init; }
        public JobStatus Status { get; init; }
        public DateTime CreatedAt { get; init; }
        public DateTime? CompletedAt { get; init; }
        public int CompletionPercentage { get; init; }
        public List<ServiceType> RequestedServices { get; init; } = new();
        public List<ServiceResultDto> Results { get; init; } = new();
    }

    public record ServiceResultDto
    {
        public ServiceType ServiceType { get; init; }
        public bool Success { get; init; }
        public string? Data { get; init; }
        public string? ErrorMessage { get; init; }
        public DateTime CompletedAt { get; init; }
        public int DurationMs { get; init; }
    }
}
