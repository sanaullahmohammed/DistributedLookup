namespace DistributedLookup.Contracts;

/// <summary>
/// Enum representing the type of storage backend for worker results.
/// Enables pluggable storage backends (Redis now, S3/DynamoDB in future).
/// </summary>
public enum StorageType
{
    Redis,
    S3,
    DynamoDB,
    FileSystem,
    AzureBlob
}
