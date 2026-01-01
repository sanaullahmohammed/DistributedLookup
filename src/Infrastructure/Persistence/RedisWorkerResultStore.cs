using System.Text.Json;
using DistributedLookup.Application.Workers;
using DistributedLookup.Contracts;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Infrastructure.Configuration;
using Microsoft.Extensions.Options;
using StackExchange.Redis;

namespace DistributedLookup.Infrastructure.Persistence;

/// <summary>
/// Redis implementation of IWorkerResultStore.
/// Stores worker results independently from saga state.
/// Key pattern: worker-result:{jobId}:{serviceType}
/// </summary>
public class RedisWorkerResultStore : IWorkerResultStore
{
    private readonly IConnectionMultiplexer _redis;
    private readonly RedisWorkerResultStoreOptions _options;
    private readonly JsonSerializerOptions _jsonOptions;

    public StorageType StorageType => StorageType.Redis;

    public RedisWorkerResultStore(
        IConnectionMultiplexer redis,
        IOptions<RedisWorkerResultStoreOptions> options)
    {
        _redis = redis;
        _options = options.Value;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = false
        };
    }

    public async Task<ResultLocation> SaveResultAsync(
        string jobId,
        ServiceType serviceType,
        JsonDocument data,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(jobId, serviceType);
        var db = _redis.GetDatabase(_options.Database);

        var workerResult = new WorkerResultDto
        {
            JobId = jobId,
            ServiceType = serviceType,
            Success = true,
            Duration = duration,
            CompletedAt = DateTime.UtcNow,
            Data = data.RootElement.ToString()
        };

        var json = JsonSerializer.Serialize(workerResult, _jsonOptions);
        await db.StringSetAsync(key, json, _options.Ttl);

        return new RedisResultLocation
        {
            Key = key,
            Database = _options.Database,
            Ttl = _options.Ttl
        };
    }

    public async Task<ResultLocation> SaveFailureAsync(
        string jobId,
        ServiceType serviceType,
        string errorMessage,
        TimeSpan duration,
        CancellationToken cancellationToken = default)
    {
        var key = BuildKey(jobId, serviceType);
        var db = _redis.GetDatabase(_options.Database);

        var workerResult = new WorkerResultDto
        {
            JobId = jobId,
            ServiceType = serviceType,
            Success = false,
            ErrorMessage = errorMessage,
            Duration = duration,
            CompletedAt = DateTime.UtcNow,
            Data = null
        };

        var json = JsonSerializer.Serialize(workerResult, _jsonOptions);
        await db.StringSetAsync(key, json, _options.Ttl);

        return new RedisResultLocation
        {
            Key = key,
            Database = _options.Database,
            Ttl = _options.Ttl
        };
    }

    private static string BuildKey(string jobId, ServiceType serviceType)
    {
        return $"worker-result:{jobId}:{serviceType}";
    }

    /// <summary>
    /// Internal DTO for JSON serialization (JsonDocument doesn't serialize well directly)
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
