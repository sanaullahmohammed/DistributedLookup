using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Application.Saga;
using StackExchange.Redis;

namespace DistributedLookup.Infrastructure.Persistence;

/// <summary>
/// Redis implementation of ISagaStateRepository.
/// Reads saga state directly from Redis using the MassTransit saga key pattern.
/// </summary>
public class RedisSagaStateRepository : ISagaStateRepository
{
    private readonly IConnectionMultiplexer _redis;
    private readonly JsonSerializerOptions _jsonOptions;

    public RedisSagaStateRepository(IConnectionMultiplexer redis)
    {
        _redis = redis;
        _jsonOptions = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            PropertyNameCaseInsensitive = true
        };
    }

    public async Task<LookupJobState?> GetByJobIdAsync(string jobId, CancellationToken cancellationToken = default)
    {
        var db = _redis.GetDatabase();
        
        // MassTransit Redis saga repository uses this key pattern
        // The key prefix is configured in Program.cs as "saga"
        var key = $"saga:{jobId}";
        var json = await db.StringGetAsync(key);

        if (json.IsNullOrEmpty)
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<LookupJobState>(json.ToString(), _jsonOptions);
        }
        catch (JsonException)
        {
            // Handle potential deserialization issues with old schema
            return null;
        }
    }
}
