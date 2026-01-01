using DistributedLookup.Application.Interfaces;
using DistributedLookup.Application.Saga;
using DistributedLookup.Domain.Entities;
using Microsoft.Extensions.Logging;

namespace DistributedLookup.Application.UseCases;

/// <summary>
/// Use case for retrieving job status.
/// Follows CQRS pattern - this is a query handler.
/// 
/// Aggregates data from two sources:
/// 1. IJobRepository - job metadata (target, services, timestamps)
/// 2. IWorkerResultReader - reads results stored by workers (read-only)
/// 
/// Saga state is used only for orchestration progress (pending/completed counts).
/// Saga does NOT store any result data.
/// </summary>
public class GetJobStatus(
    IJobRepository jobRepository,
    ISagaStateRepository sagaStateRepository,
    IWorkerResultReader resultReader,
    ILogger<GetJobStatus> logger)
{
    private readonly IJobRepository _jobRepository = jobRepository;
    private readonly ISagaStateRepository _sagaStateRepository = sagaStateRepository;
    private readonly IWorkerResultReader _resultReader = resultReader;
    private readonly ILogger<GetJobStatus> _logger = logger;

    public async Task<Response?> ExecuteAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var job = await _jobRepository.GetByIdAsync(jobId, cancellationToken);
        
        if (job == null)
        {
            return null;
        }

        // Fetch saga state for orchestration progress (may be null if saga completed and was removed)
        var sagaState = await _sagaStateRepository.GetByJobIdAsync(jobId, cancellationToken);
        
        var results = new List<ServiceResultDto>();
        var warnings = new List<string>();

        // Always query worker results directly from store
        // Saga does NOT store results - only orchestration state
        foreach (var serviceType in job.RequestedServices)
        {
            var resultDto = await FetchResultFromReader(
                jobId,
                serviceType,
                warnings,
                cancellationToken);
            
            if (resultDto != null)
            {
                results.Add(resultDto);
            }
        }

        // Determine status based on saga state (if available) or results
        var status = DetermineJobStatus(sagaState, job, results);
        var completedAt = DetermineCompletedAt(sagaState, job, results);
        var completionPercentage = CalculateCompletionPercentage(sagaState, job, results);

        return new Response
        {
            JobId = job.JobId,
            Target = job.Target,
            TargetType = job.TargetType,
            Status = status,
            CreatedAt = job.CreatedAt,
            CompletedAt = completedAt,
            CompletionPercentage = completionPercentage,
            RequestedServices = job.RequestedServices.ToList(),
            Results = results,
            Warnings = warnings.Count > 0 ? warnings : null
        };
    }

    private async Task<ServiceResultDto?> FetchResultFromReader(
        string jobId,
        ServiceType serviceType,
        List<string> warnings,
        CancellationToken cancellationToken)
    {
        try
        {
            var workerResult = await _resultReader.GetResultAsync(jobId, serviceType, cancellationToken);

            if (workerResult == null)
            {
                // Result not yet available or expired
                return null;
            }

            return new ServiceResultDto
            {
                ServiceType = serviceType,
                Success = workerResult.Success,
                Data = workerResult.Data?.RootElement.ToString(),
                ErrorMessage = workerResult.ErrorMessage,
                CompletedAt = workerResult.CompletedAt,
                DurationMs = (int)workerResult.Duration.TotalMilliseconds
            };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch result for {ServiceType}", serviceType);
            warnings.Add($"Failed to retrieve result for {serviceType}: {ex.Message}");
            return null;
        }
    }

    private static JobStatus DetermineJobStatus(LookupJobState? sagaState, LookupJob job, List<ServiceResultDto> results)
    {
        if (sagaState != null)
        {
            return sagaState.CurrentState switch
            {
                "Processing" => JobStatus.Processing,
                "Completed" => JobStatus.Completed,
                "Final" => JobStatus.Completed,
                _ => JobStatus.Pending
            };
        }

        // No saga state - determine from results
        if (results.Count == 0)
        {
            return JobStatus.Pending;
        }

        if (results.Count >= job.RequestedServices.Count)
        {
            return JobStatus.Completed;
        }

        return JobStatus.Processing;
    }

    private static DateTime? DetermineCompletedAt(LookupJobState? sagaState, LookupJob job, List<ServiceResultDto> results)
    {
        if (sagaState?.CompletedAt != null)
        {
            return sagaState.CompletedAt;
        }

        if (job.CompletedAt != null)
        {
            return job.CompletedAt;
        }

        // If all results are in, use the latest completion time
        if (results.Count >= job.RequestedServices.Count && results.Count > 0)
        {
            return results.Max(r => r.CompletedAt);
        }

        return null;
    }

    private static int CalculateCompletionPercentage(LookupJobState? sagaState, LookupJob job, List<ServiceResultDto> results)
    {
        if (sagaState != null)
        {
            var total = sagaState.PendingServices.Count + sagaState.CompletedServices.Count;
            if (total == 0) return 0;
            return sagaState.CompletedServices.Count * 100 / total;
        }

        // No saga state - calculate from results
        var requestedCount = job.RequestedServices.Count;
        if (requestedCount == 0) return 0;
        
        return (results.Count * 100) / requestedCount;
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
        
        /// <summary>
        /// Warnings about partial data (e.g., expired results).
        /// Null if no warnings.
        /// </summary>
        public List<string>? Warnings { get; init; }
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
