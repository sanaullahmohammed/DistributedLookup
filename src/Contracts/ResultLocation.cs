using System.Text.Json.Serialization;

namespace DistributedLookup.Contracts;

/// <summary>
/// Polymorphic base class for result locations.
/// Each storage backend has its own derived type with type-safe properties.
/// Uses JSON polymorphism for saga state serialization.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "$type")]
[JsonDerivedType(typeof(RedisResultLocation), "redis")]
[JsonDerivedType(typeof(S3ResultLocation), "s3")]
[JsonDerivedType(typeof(DynamoDBResultLocation), "dynamodb")]
[JsonDerivedType(typeof(FileSystemResultLocation), "filesystem")]
[JsonDerivedType(typeof(AzureBlobResultLocation), "azureblob")]
public abstract record ResultLocation
{
    /// <summary>
    /// The storage type this location refers to.
    /// Used by the resolver to get the correct store implementation.
    /// </summary>
    public abstract StorageType StorageType { get; }
}

/// <summary>
/// Location metadata for results stored in Redis.
/// </summary>
public record RedisResultLocation : ResultLocation
{
    public override StorageType StorageType => StorageType.Redis;
    
    /// <summary>
    /// The Redis key where the result is stored.
    /// Format: worker-result:{jobId}:{serviceType}
    /// </summary>
    public required string Key { get; init; }
    
    /// <summary>
    /// The Redis database number.
    /// </summary>
    public int Database { get; init; }
    
    /// <summary>
    /// Time-to-live for the stored result.
    /// </summary>
    public TimeSpan? Ttl { get; init; }
}

/// <summary>
/// Location metadata for results stored in S3.
/// </summary>
public record S3ResultLocation : ResultLocation
{
    public override StorageType StorageType => StorageType.S3;
    
    public required string Bucket { get; init; }
    public required string Key { get; init; }
    public string? Region { get; init; }
    public string? PresignedUrl { get; init; }
}

/// <summary>
/// Location metadata for results stored in DynamoDB.
/// </summary>
public record DynamoDBResultLocation : ResultLocation
{
    public override StorageType StorageType => StorageType.DynamoDB;
    
    public required string TableName { get; init; }
    public required string PartitionKey { get; init; }
    public string? SortKey { get; init; }
    public string? Region { get; init; }
}

/// <summary>
/// Location metadata for results stored in the file system.
/// </summary>
public record FileSystemResultLocation : ResultLocation
{
    public override StorageType StorageType => StorageType.FileSystem;
    
    public required string Path { get; init; }
}

/// <summary>
/// Location metadata for results stored in Azure Blob Storage.
/// </summary>
public record AzureBlobResultLocation : ResultLocation
{
    public override StorageType StorageType => StorageType.AzureBlob;
    
    public required string ContainerName { get; init; }
    public required string BlobName { get; init; }
    public string? SasUrl { get; init; }
}
