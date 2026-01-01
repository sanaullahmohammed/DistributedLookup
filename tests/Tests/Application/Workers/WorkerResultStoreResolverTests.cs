using System.Collections;
using System.Reflection;
using System.Text.Json;
using DistributedLookup.Application.Workers;
using DistributedLookup.Contracts;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using Xunit;

namespace Tests.Application.Workers;

public class WorkerResultStoreResolverTests
{
    [Fact]
    public void GetStore_WhenStoreRegistered_ShouldResolveFromServiceProvider_AndCacheInstance()
    {
        // Arrange
        var storeType = typeof(FakeStore1);
        var storeInstance = new FakeStore1();

        var options = CreateOptions(
            defaultStorageType: StorageType.Redis,
            (StorageType.Redis, storeType));

        var sp = new Mock<IServiceProvider>(MockBehavior.Strict);
        sp.Setup(x => x.GetService(storeType)).Returns(storeInstance);

        var sut = new WorkerResultStoreResolver(sp.Object, Options.Create(options));

        // Act
        var first = sut.GetStore(StorageType.Redis);
        var second = sut.GetStore(StorageType.Redis);

        // Assert
        first.Should().BeSameAs(storeInstance);
        second.Should().BeSameAs(storeInstance);

        sp.Verify(x => x.GetService(storeType), Times.Once);
        sp.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetStore_WhenNoStoreRegistered_ShouldThrow_AndNotCallServiceProvider()
    {
        // Arrange
        var options = CreateOptions(defaultStorageType: StorageType.Redis); // no mappings
        var sp = new Mock<IServiceProvider>(MockBehavior.Strict);

        var sut = new WorkerResultStoreResolver(sp.Object, Options.Create(options));

        // Act
        Action act = () => sut.GetStore(StorageType.Redis);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage($"No worker result store registered for storage type: {StorageType.Redis}*");

        sp.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetDefaultStore_ShouldUseDefaultStorageType()
    {
        // Arrange
        var storeType = typeof(FakeStore1);
        var storeInstance = new FakeStore1();

        var options = CreateOptions(
            defaultStorageType: StorageType.Redis,
            (StorageType.Redis, storeType));

        var sp = new Mock<IServiceProvider>(MockBehavior.Strict);
        sp.Setup(x => x.GetService(storeType)).Returns(storeInstance);

        var sut = new WorkerResultStoreResolver(sp.Object, Options.Create(options));

        // Act
        var result = sut.GetDefaultStore();

        // Assert
        result.Should().BeSameAs(storeInstance);
        sp.Verify(x => x.GetService(storeType), Times.Once);
        sp.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetStore_ShouldCachePerStorageType_WhenMultipleStorageTypesExist()
    {
        // If your enum only has a single value (e.g. only Redis), caching is already covered above.
        var values = Enum.GetValues<StorageType>();
        if (values.Length < 2)
            return;

        var firstType = values[0];
        var secondType = values.First(v => !EqualityComparer<StorageType>.Default.Equals(v, firstType));

        var store1Type = typeof(FakeStore1);
        var store2Type = typeof(FakeStore2);

        var store1 = new FakeStore1();
        var store2 = new FakeStore2();

        var options = CreateOptions(
            defaultStorageType: firstType,
            (firstType, store1Type),
            (secondType, store2Type));

        var sp = new Mock<IServiceProvider>(MockBehavior.Strict);
        sp.Setup(x => x.GetService(store1Type)).Returns(store1);
        sp.Setup(x => x.GetService(store2Type)).Returns(store2);

        var sut = new WorkerResultStoreResolver(sp.Object, Options.Create(options));

        // Act
        var a1 = sut.GetStore(firstType);
        var b1 = sut.GetStore(secondType);
        var a2 = sut.GetStore(firstType);

        // Assert
        a1.Should().BeSameAs(store1);
        a2.Should().BeSameAs(store1);
        b1.Should().BeSameAs(store2);

        sp.Verify(x => x.GetService(store1Type), Times.Once);
        sp.Verify(x => x.GetService(store2Type), Times.Once);
        sp.VerifyNoOtherCalls();
    }

    // -----------------------
    // Helpers: create & populate WorkerResultStoreOptions without assuming
    // exact internal representation (method vs dictionary vs string mapping)
    // -----------------------

    private static WorkerResultStoreOptions CreateOptions(
        StorageType defaultStorageType,
        params (StorageType storageType, Type storeType)[] stores)
    {
        var options = Activator.CreateInstance<WorkerResultStoreOptions>()
            ?? throw new InvalidOperationException($"Could not create instance of {nameof(WorkerResultStoreOptions)}.");

        SetDefaultStorageType(options, defaultStorageType);

        foreach (var (storageType, storeType) in stores)
            RegisterStoreType(options, storageType, storeType);

        // Sanity check: mappings we added must resolve via GetStoreType
        foreach (var (storageType, storeType) in stores)
        {
            var resolved = options.GetStoreType(storageType);
            if (resolved != storeType)
            {
                throw new InvalidOperationException(
                    $"{nameof(WorkerResultStoreOptions)}.{nameof(WorkerResultStoreOptions.GetStoreType)}({storageType}) " +
                    $"returned '{resolved}', expected '{storeType}'.");
            }
        }

        return options;
    }

    private static void SetDefaultStorageType(WorkerResultStoreOptions options, StorageType value)
    {
        var t = typeof(WorkerResultStoreOptions);

        var prop = t.GetProperty("DefaultStorageType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null && prop.CanWrite)
        {
            prop.SetValue(options, value);
            return;
        }

        var field = t.GetField("DefaultStorageType", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(options, value);
            return;
        }

        throw new InvalidOperationException($"Could not set DefaultStorageType on {nameof(WorkerResultStoreOptions)}.");
    }

    private static void RegisterStoreType(WorkerResultStoreOptions options, StorageType storageType, Type storeType)
    {
        var t = typeof(WorkerResultStoreOptions);

        // Try a public method with signature (StorageType, Type)
        var methods = t.GetMethods(BindingFlags.Instance | BindingFlags.Public);
        var methodWithType = methods.FirstOrDefault(m =>
        {
            var p = m.GetParameters();
            return p.Length == 2 &&
                   p[0].ParameterType == typeof(StorageType) &&
                   p[1].ParameterType == typeof(Type);
        });

        if (methodWithType != null)
        {
            methodWithType.Invoke(options, new object?[] { storageType, storeType });
            return;
        }

        // Try a public method with signature (StorageType, string)
        var methodWithString = methods.FirstOrDefault(m =>
        {
            var p = m.GetParameters();
            return p.Length == 2 &&
                   p[0].ParameterType == typeof(StorageType) &&
                   p[1].ParameterType == typeof(string);
        });

        if (methodWithString != null)
        {
            methodWithString.Invoke(options, new object?[] { storageType, storeType.AssemblyQualifiedName! });
            return;
        }

        // Try dictionary-like property/field storage (supports value Type or string).
        if (TryWriteMappingToDictionaryProperty(options, storageType, storeType))
            return;

        if (TryWriteMappingToDictionaryField(options, storageType, storeType))
            return;

        throw new InvalidOperationException(
            $"Unable to register store mapping on {nameof(WorkerResultStoreOptions)}. " +
            "Expose a registration method (StorageType, Type/string) or a dictionary-like member.");
    }

    private static bool TryWriteMappingToDictionaryProperty(
        WorkerResultStoreOptions options,
        StorageType storageType,
        Type storeType)
    {
        var t = typeof(WorkerResultStoreOptions);

        foreach (var prop in t.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!TryGetDictionaryShape(prop.PropertyType, out var keyType, out var valueType))
                continue;

            if (!TryBuildKeyValue(storageType, storeType, keyType, valueType, out var keyObj, out var valueObj))
                continue;

            var dict = prop.GetValue(options);
            if (dict == null)
            {
                if (!prop.CanWrite)
                    continue;

                dict = Activator.CreateInstance(prop.PropertyType);
                if (dict == null)
                    continue;

                prop.SetValue(options, dict);
            }

            if (TrySetDictionaryEntry(dict, keyObj!, valueObj!))
                return true;
        }

        return false;
    }

    private static bool TryWriteMappingToDictionaryField(
        WorkerResultStoreOptions options,
        StorageType storageType,
        Type storeType)
    {
        var t = typeof(WorkerResultStoreOptions);

        foreach (var field in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
        {
            if (!TryGetDictionaryShape(field.FieldType, out var keyType, out var valueType))
                continue;

            if (!TryBuildKeyValue(storageType, storeType, keyType, valueType, out var keyObj, out var valueObj))
                continue;

            var dict = field.GetValue(options);
            if (dict == null)
            {
                dict = Activator.CreateInstance(field.FieldType);
                if (dict == null)
                    continue;

                field.SetValue(options, dict);
            }

            if (TrySetDictionaryEntry(dict, keyObj!, valueObj!))
                return true;
        }

        return false;
    }

    private static bool TryGetDictionaryShape(Type type, out Type keyType, out Type valueType)
    {
        // Look for IDictionary<TKey, TValue> on the type itself or its interfaces
        Type? iface = null;

        if (type.IsInterface && type.IsGenericType && type.GetGenericTypeDefinition() == typeof(IDictionary<,>))
        {
            iface = type;
        }
        else
        {
            iface = type.GetInterfaces().FirstOrDefault(i =>
                i.IsGenericType && i.GetGenericTypeDefinition() == typeof(IDictionary<,>));
        }

        if (iface == null)
        {
            keyType = typeof(object);
            valueType = typeof(object);
            return false;
        }

        var args = iface.GetGenericArguments();
        keyType = args[0];
        valueType = args[1];
        return true;
    }

    private static bool TryBuildKeyValue(
        StorageType storageType,
        Type storeType,
        Type keyType,
        Type valueType,
        out object? keyObj,
        out object? valueObj)
    {
        keyObj = null;
        valueObj = null;

        if (keyType == typeof(StorageType))
            keyObj = storageType;
        else if (keyType == typeof(string))
            keyObj = storageType.ToString();
        else
            return false;

        if (valueType == typeof(Type))
            valueObj = storeType;
        else if (valueType == typeof(string))
            valueObj = storeType.AssemblyQualifiedName ?? storeType.FullName ?? storeType.Name;
        else
            return false;

        return true;
    }

    private static bool TrySetDictionaryEntry(object dict, object key, object value)
    {
        // Generic indexer "Item" exists on IDictionary<TKey, TValue> implementations
        var itemProp = dict.GetType().GetProperty("Item", BindingFlags.Instance | BindingFlags.Public);
        if (itemProp != null && itemProp.CanWrite)
        {
            itemProp.SetValue(dict, value, new[] { key });
            return true;
        }

        // Non-generic IDictionary fallback
        if (dict is IDictionary nonGeneric)
        {
            nonGeneric[key] = value;
            return true;
        }

        return false;
    }

    // -----------------------
    // Fake stores for testing
    // -----------------------

    private sealed class FakeStore1 : IWorkerResultStore
    {
        public StorageType StorageType => StorageType.Redis;

        public Task<ResultLocation> SaveResultAsync(
            string jobId,
            ServiceType serviceType,
            JsonDocument data,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ResultLocation> SaveFailureAsync(
            string jobId,
            ServiceType serviceType,
            string errorMessage,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }

    private sealed class FakeStore2 : IWorkerResultStore
    {
        public StorageType StorageType => StorageType.Redis;

        public Task<ResultLocation> SaveResultAsync(
            string jobId,
            ServiceType serviceType,
            JsonDocument data,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();

        public Task<ResultLocation> SaveFailureAsync(
            string jobId,
            ServiceType serviceType,
            string errorMessage,
            TimeSpan duration,
            CancellationToken cancellationToken = default) =>
            throw new NotImplementedException();
    }
}
