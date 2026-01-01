using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Domain.Entities;
using StackExchange.Redis;

namespace DistributedLookup.Infrastructure.Persistence;

/// <summary>
/// Redis implementation of IWorkerResultReader.
/// Used by the API to read worker results stored by workers.
/// This is read-only - workers use RedisWorkerResultStore to write.
/// </summary>
public class RedisWorkerResultReader : IWorkerResultReader
{
    private readonly IConnectionMultiplexer _redis;
    private readonly JsonSerializerOptions _jsonOptions;

    public RedisWorkerResultReader(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<WorkerResultData?> GetResultAsync(
        string jobId,
        ServiceType serviceType,
        CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        var key = $"worker-result:{jobId}:{serviceType}";
        
        var json = await db.StringGetAsync(key);
        if (json.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            var dto = JsonSerializer.Deserialize<WorkerResultDto>(json.ToString(), _jsonOptions);
            if (dto == null)
            {
                return null;
            }

            return new WorkerResultData
            {
                JobId = dto.JobId,
                ServiceType = dto.ServiceType,
                Success = dto.Success,
                ErrorMessage = dto.ErrorMessage,
                Duration = dto.Duration,
                CompletedAt = dto.CompletedAt,
                Data = dto.Data != null ? JsonDocument.Parse(dto.Data) : null
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// DTO matching the format written by RedisWorkerResultStore in workers.
    /// </summary>
    private class WorkerResultDto
    {
        public string JobId { get; set; } = string.Empty;
        public ServiceType ServiceType { get; set; }
        public bool Success { get; set; }
        public string? ErrorMessage { get; set; }
        public TimeSpan Duration { get; set; }
        public DateTime CompletedAt { get; set; }
        public string? Data { get; set; }
    }
}
