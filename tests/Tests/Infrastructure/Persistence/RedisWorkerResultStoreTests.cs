using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Contracts;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Infrastructure.Configuration;
using DistributedLookup.Infrastructure.Persistence;
using FluentAssertions;
using Microsoft.Extensions.Options;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Tests.Infrastructure.Persistence;

public class RedisWorkerResultStoreTests
{
    [Fact]
    public void StorageType_ShouldBeRedis()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var options = Options.Create(new RedisWorkerResultStoreOptions
        {
            Database = 2,
            Ttl = TimeSpan.FromMinutes(15)
        });

        var sut = new RedisWorkerResultStore(h.Connection, options);

        // Act + Assert
        sut.StorageType.Should().Be(StorageType.Redis);
    }

    [Fact]
    public async Task SaveResultAsync_ShouldStoreSuccessPayload_WithExpectedKey_Db_Ttl_AndReturnRedisLocation()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var storeOptions = new RedisWorkerResultStoreOptions
        {
            Database = 3,
            Ttl = TimeSpan.FromMinutes(10)
        };

        var sut = new RedisWorkerResultStore(h.Connection, Options.Create(storeOptions));

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.GeoIP;
        var expectedKey = $"worker-result:{jobId}:{serviceType}";

        using var payload = JsonDocument.Parse(@"{""country"":""US"",""asn"":""AS123""}");
        var duration = TimeSpan.FromMilliseconds(123);

        var before = DateTime.UtcNow;

        // Act
        var location = await sut.SaveResultAsync(jobId, serviceType, payload, duration, CancellationToken.None);

        var after = DateTime.UtcNow;

        // Assert - returned location
        location.Should().BeOfType<RedisResultLocation>();
        var redisLoc = (RedisResultLocation)location;
        redisLoc.Key.Should().Be(expectedKey);
        redisLoc.Database.Should().Be(storeOptions.Database);
        redisLoc.Ttl.Should().Be(storeOptions.Ttl);

        // Assert - redis calls
        h.MuxMock.Verify(m => m.GetDatabase(
                It.Is<int>(db => db == storeOptions.Database),
                It.IsAny<object>()),
            Times.Once);

        h.DbMock.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedKey),
                It.IsAny<RedisValue>(),
                It.Is<TimeSpan?>(t => t == storeOptions.Ttl),
                It.Is<bool>(keepTtl => keepTtl == false),
                It.Is<When>(w => w == When.Always),
                It.Is<CommandFlags>(f => f == CommandFlags.None)),
            Times.Once);

        // Assert - stored JSON payload
        h.Store.Should().ContainKey(expectedKey);
        var storedJson = h.Store[expectedKey].Value.ToString();
        storedJson.Should().NotBeNullOrWhiteSpace();

        using var storedDoc = JsonDocument.Parse(storedJson);
        var root = storedDoc.RootElement;

        root.GetProperty("jobId").GetString().Should().Be(jobId);
        ReadServiceType(root.GetProperty("serviceType")).Should().Be(serviceType);
        root.GetProperty("success").GetBoolean().Should().BeTrue();

        // errorMessage should be null / missing for success
        root.TryGetProperty("errorMessage", out var errEl).Should().BeTrue();
        errEl.ValueKind.Should().Be(JsonValueKind.Null);

        // TimeSpan is serialized as string (e.g., "00:00:00.1230000")
        var durStr = root.GetProperty("duration").GetString();
        durStr.Should().NotBeNull();
        TimeSpan.Parse(durStr!, CultureInfo.InvariantCulture).Should().Be(duration);

        var completedAt = root.GetProperty("completedAt").GetDateTime();
        completedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after.AddSeconds(1));

        // Data is stored as a JSON string (RootElement.ToString())
        root.GetProperty("data").GetString().Should().Be(payload.RootElement.ToString());
    }

    [Fact]
    public async Task SaveFailureAsync_ShouldStoreFailurePayload_WithExpectedKey_Db_Ttl_AndReturnRedisLocation()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var storeOptions = new RedisWorkerResultStoreOptions
        {
            Database = 1,
            Ttl = TimeSpan.FromHours(1)
        };

        var sut = new RedisWorkerResultStore(h.Connection, Options.Create(storeOptions));

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.Ping;
        var expectedKey = $"worker-result:{jobId}:{serviceType}";

        var errorMessage = "boom";
        var duration = TimeSpan.FromMilliseconds(456);

        var before = DateTime.UtcNow;

        // Act
        var location = await sut.SaveFailureAsync(jobId, serviceType, errorMessage, duration, CancellationToken.None);

        var after = DateTime.UtcNow;

        // Assert - returned location
        location.Should().BeOfType<RedisResultLocation>();
        var redisLoc = (RedisResultLocation)location;
        redisLoc.Key.Should().Be(expectedKey);
        redisLoc.Database.Should().Be(storeOptions.Database);
        redisLoc.Ttl.Should().Be(storeOptions.Ttl);

        // Assert - redis calls
        h.MuxMock.Verify(m => m.GetDatabase(
                It.Is<int>(db => db == storeOptions.Database),
                It.IsAny<object>()),
            Times.Once);

        h.DbMock.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedKey),
                It.IsAny<RedisValue>(),
                It.Is<TimeSpan?>(t => t == storeOptions.Ttl),
                It.Is<bool>(keepTtl => keepTtl == false),
                It.Is<When>(w => w == When.Always),
                It.Is<CommandFlags>(f => f == CommandFlags.None)),
            Times.Once);

        // Assert - stored JSON payload
        h.Store.Should().ContainKey(expectedKey);
        var storedJson = h.Store[expectedKey].Value.ToString();
        storedJson.Should().NotBeNullOrWhiteSpace();

        using var storedDoc = JsonDocument.Parse(storedJson);
        var root = storedDoc.RootElement;

        root.GetProperty("jobId").GetString().Should().Be(jobId);
        ReadServiceType(root.GetProperty("serviceType")).Should().Be(serviceType);
        root.GetProperty("success").GetBoolean().Should().BeFalse();

        root.GetProperty("errorMessage").GetString().Should().Be(errorMessage);

        var durStr = root.GetProperty("duration").GetString();
        durStr.Should().NotBeNull();
        TimeSpan.Parse(durStr!, CultureInfo.InvariantCulture).Should().Be(duration);

        var completedAt = root.GetProperty("completedAt").GetDateTime();
        completedAt.Should().BeOnOrAfter(before).And.BeOnOrBefore(after.AddSeconds(1));

        // Data should be null for failures
        root.TryGetProperty("data", out var dataEl).Should().BeTrue();
        dataEl.ValueKind.Should().Be(JsonValueKind.Null);
    }

    private static ServiceType ReadServiceType(JsonElement el)
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => (ServiceType)el.GetInt32(),
            JsonValueKind.String => Enum.Parse<ServiceType>(el.GetString()!, ignoreCase: true),
            _ => throw new InvalidOperationException($"Unexpected JSON kind for serviceType: {el.ValueKind}")
        };
    }

    private sealed class RedisMoqHarness
    {
        public Mock<IDatabase> DbMock { get; } = new(MockBehavior.Strict);
        public Mock<IConnectionMultiplexer> MuxMock { get; } = new(MockBehavior.Strict);

        public IConnectionMultiplexer Connection => MuxMock.Object;

        public sealed record StoredEntry(RedisValue Value, TimeSpan? Expiry);
        public System.Collections.Generic.Dictionary<string, StoredEntry> Store { get; } = new();

        public RedisMoqHarness()
        {
            // IConnectionMultiplexer.GetDatabase(int db = -1, object? asyncState = null)
            MuxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Returns(DbMock.Object);

            // IDatabase.StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null,
            //                          bool keepTtl = false, When when = Always, CommandFlags flags = None)
            DbMock.Setup(d => d.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<bool>(),
                    It.IsAny<When>(),
                    It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((key, value, expiry, _, __, ___) =>
                {
                    Store[key.ToString()] = new StoredEntry(value, expiry);
                })
                .ReturnsAsync(true);
        }
    }
}
