using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Domain.Entities;
using StackExchange.Redis;

namespace DistributedLookup.Infrastructure.Persistence;

/// <summary>
/// Redis implementation of job repository.
/// Uses Redis for fast, volatile storage with JSON serialization.
/// </summary>
public class RedisJobRepository : IJobRepository
{
    private readonly IConnectionMultiplexer _redis;
    private const string KeyPrefix = "lookup:job:";

    public RedisJobRepository(IConnectionMultiplexer redis)
    {
        _redis = redis;
    }

    public async Task<LookupJob?> GetByIdAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var key = GetKey(jobId);
        
        var json = await db.StringGetAsync(key);
        if (json.IsNullOrEmpty)
        {
            return null;
        }

        return DeserializeJob(json!);
    }

    public async Task SaveAsync(LookupJob job, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var key = GetKey(job.JobId);
        var json = SerializeJob(job);
        
        // Set with 24-hour expiration
        await db.StringSetAsync(key, json, TimeSpan.FromHours(24));
    }

    public async Task<IEnumerable<LookupJob>> GetPendingJobsAsync(CancellationToken cancellationToken = default)
    {
        // For simplicity, not implementing scan pattern here
        // In production, you'd use SCAN to iterate keys
        return Array.Empty<LookupJob>();
    }

    private static string GetKey(string jobId) => $"{KeyPrefix}{jobId}";

    private static string SerializeJob(LookupJob job)
    {
        var dto = new JobDto
        {
            JobId = job.JobId,
            Target = job.Target,
            TargetType = job.TargetType,
            Status = job.Status,
            CreatedAt = job.CreatedAt,
            CompletedAt = job.CompletedAt,
            RequestedServices = job.RequestedServices.ToArray(),
            Results = job.Results.Values.Select(r => new ResultDto
            {
                ServiceType = r.ServiceType,
                Success = r.Success,
                Data = r.Data?.RootElement.ToString(),
                ErrorMessage = r.ErrorMessage,
                CompletedAt = r.CompletedAt,
                DurationMs = (long)r.Duration.TotalMilliseconds
            }).ToArray()
        };

        return JsonSerializer.Serialize(dto);
    }

    private static LookupJob DeserializeJob(string json)
    {
        var dto = JsonSerializer.Deserialize<JobDto>(json);
        if (dto == null)
        {
            throw new InvalidOperationException("Failed to deserialize job");
        }

        // Reconstruct domain entity
        var job = new LookupJob(dto.JobId, dto.Target, dto.TargetType, dto.RequestedServices);

        // Use reflection to set private fields (not ideal, but works for demo)
        var statusField = typeof(LookupJob).GetField("<Status>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        statusField?.SetValue(job, dto.Status);

        var completedField = typeof(LookupJob).GetField("<CompletedAt>k__BackingField",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
        completedField?.SetValue(job, dto.CompletedAt);

        // Add results
        foreach (var result in dto.Results)
        {
            var serviceResult = result.Success
                ? ServiceResult.CreateSuccess(
                    result.ServiceType,
                    result.Data ?? "{}",
                    TimeSpan.FromMilliseconds(result.DurationMs))
                : ServiceResult.CreateFailure(
                    result.ServiceType,
                    result.ErrorMessage ?? "Unknown error",
                    TimeSpan.FromMilliseconds(result.DurationMs));

            // Add result (this will bypass validation if job is already complete)
            var resultsField = typeof(LookupJob).GetField("_results",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            var resultsDict = resultsField?.GetValue(job) as Dictionary<ServiceType, ServiceResult>;
            resultsDict?.Add(result.ServiceType, serviceResult);
        }

        return job;
    }

    // DTOs for serialization
    private class JobDto
    {
        public string JobId { get; set; } = string.Empty;
        public string Target { get; set; } = string.Empty;
        public LookupTarget TargetType { get; set; }
        public JobStatus Status { get; set; }
        public DateTime CreatedAt { get; set; }
        public DateTime? CompletedAt { get; set; }
        public ServiceType[] RequestedServices { get; set; } = Array.Empty<ServiceType>();
        public ResultDto[] Results { get; set; } = Array.Empty<ResultDto>();
    }

    private class ResultDto
    {
        public ServiceType ServiceType { get; set; }
        public bool Success { get; set; }
        public string? Data { get; set; }
        public string? ErrorMessage { get; set; }
        public DateTime CompletedAt { get; set; }
        public long DurationMs { get; set; }
    }
}
