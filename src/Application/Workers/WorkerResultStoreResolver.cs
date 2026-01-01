using DistributedLookup.Contracts;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;

namespace DistributedLookup.Application.Workers;

/// <summary>
/// Default implementation of IWorkerResultStoreResolver.
/// Resolves store implementations from DI container based on registered storage types.
/// </summary>
public class WorkerResultStoreResolver(
    IServiceProvider serviceProvider,
    IOptions<WorkerResultStoreOptions> options) : IWorkerResultStoreResolver
{
    private readonly IServiceProvider _serviceProvider = serviceProvider;
    private readonly WorkerResultStoreOptions _options = options.Value;
    private readonly Dictionary<StorageType, IWorkerResultStore> _storeCache = new();

    public IWorkerResultStore GetStore(StorageType storageType)
    {
        if (_storeCache.TryGetValue(storageType, out var cachedStore))
        {
            return cachedStore;
        }

        var storeType = _options.GetStoreType(storageType);
        if (storeType == null)
        {
            throw new InvalidOperationException(
                $"No worker result store registered for storage type: {storageType}");
        }

        var store = (IWorkerResultStore)_serviceProvider.GetRequiredService(storeType);
        _storeCache[storageType] = store;
        return store;
    }

    public IWorkerResultStore GetDefaultStore()
    {
        return GetStore(_options.DefaultStorageType);
    }
}
