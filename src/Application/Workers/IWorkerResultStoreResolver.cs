using DistributedLookup.Contracts;

namespace DistributedLookup.Application.Workers;

/// <summary>
/// Resolves the appropriate IWorkerResultStore implementation for a given storage type.
/// Enables the API to fetch results from different storage backends without hard-coding.
/// </summary>
public interface IWorkerResultStoreResolver
{
    /// <summary>
    /// Gets the store implementation for the specified storage type.
    /// </summary>
    /// <param name="storageType">The storage type to get a store for</param>
    /// <returns>The store implementation</returns>
    /// <exception cref="InvalidOperationException">If no store is registered for the storage type</exception>
    IWorkerResultStore GetStore(StorageType storageType);
    
    /// <summary>
    /// Gets the default store implementation (used by workers).
    /// </summary>
    /// <returns>The default store implementation</returns>
    IWorkerResultStore GetDefaultStore();
}
