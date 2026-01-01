using DistributedLookup.Contracts;

namespace DistributedLookup.Application.Workers;

/// <summary>
/// Configuration options for worker result store registration.
/// Uses simple configuration pattern (not fluent builder).
/// </summary>
public class WorkerResultStoreOptions
{
    private readonly Dictionary<StorageType, Type> _registrations = new();
    
    /// <summary>
    /// The default storage type to use when workers store results.
    /// </summary>
    public StorageType DefaultStorageType { get; set; } = StorageType.Redis;
    
    /// <summary>
    /// Registers a store implementation for a storage type.
    /// </summary>
    /// <typeparam name="TStore">The store implementation type</typeparam>
    /// <param name="storageType">The storage type this store handles</param>
    public void Register<TStore>(StorageType storageType) where TStore : IWorkerResultStore
    {
        _registrations[storageType] = typeof(TStore);
    }
    
    /// <summary>
    /// Gets the store type registered for a storage type.
    /// </summary>
    /// <param name="storageType">The storage type to look up</param>
    /// <returns>The registered store type, or null if not registered</returns>
    public Type? GetStoreType(StorageType storageType)
    {
        return _registrations.TryGetValue(storageType, out var type) ? type : null;
    }
    
    /// <summary>
    /// Gets all registered store types.
    /// </summary>
    public IReadOnlyDictionary<StorageType, Type> GetRegistrations() => _registrations;
}
