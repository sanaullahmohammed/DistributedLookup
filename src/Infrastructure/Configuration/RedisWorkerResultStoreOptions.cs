namespace DistributedLookup.Infrastructure.Configuration;

/// <summary>
/// Configuration options for Redis worker result storage.
/// </summary>
public class RedisWorkerResultStoreOptions
{
    public const string SectionName = "RedisWorkerResultStore";
    
    /// <summary>
    /// The Redis database number to use for worker results.
    /// </summary>
    public int Database { get; set; } = 0;
    
    /// <summary>
    /// Time-to-live for stored results.
    /// Default is 24 hours.
    /// </summary>
    public TimeSpan Ttl { get; set; } = TimeSpan.FromHours(24);
}
