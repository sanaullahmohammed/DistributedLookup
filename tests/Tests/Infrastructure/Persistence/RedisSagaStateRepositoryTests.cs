using System;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Application.Saga;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Infrastructure.Persistence;
using FluentAssertions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Tests.Infrastructure.Persistence;

public class RedisSagaStateRepositoryTests
{
    [Fact]
    public async Task GetByJobIdAsync_WhenKeyDoesNotExist_ShouldReturnNull_AndUseSagaKeyPrefix()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisSagaStateRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var expectedKey = $"saga:{jobId}";

        // Act
        var result = await repo.GetByJobIdAsync(jobId, CancellationToken.None);

        // Assert
        result.Should().BeNull();

        h.MuxMock.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Once);
        h.DbMock.Verify(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedKey),
                It.Is<CommandFlags>(f => f == CommandFlags.None)),
            Times.Once);

        h.MuxMock.VerifyNoOtherCalls();
        h.DbMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetByJobIdAsync_WhenValueIsEmptyString_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisSagaStateRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var key = $"saga:{jobId}";

        h.Seed(key, RedisValue.EmptyString);

        // Act
        var result = await repo.GetByJobIdAsync(jobId);

        // Assert
        result.Should().BeNull();

        h.DbMock.Verify(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString() == key),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task GetByJobIdAsync_WhenValueIsInvalidJson_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisSagaStateRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var key = $"saga:{jobId}";

        // invalid JSON -> JsonSerializer throws JsonException -> repo returns null
        h.Seed(key, (RedisValue)"not json");

        // Act
        var result = await repo.GetByJobIdAsync(jobId);

        // Assert
        result.Should().BeNull();

        h.DbMock.Verify(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString() == key),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    [Fact]
    public async Task GetByJobIdAsync_WhenValueIsValidJson_ShouldDeserializeAndReturnLookupJobState()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisSagaStateRepository(h.Connection);

        // Use a GUID string so it can also be used as CorrelationId
        var jobId = Guid.NewGuid().ToString();
        var key = $"saga:{jobId}";

        var correlationId = Guid.Parse(jobId);
        var completedAt = DateTime.UtcNow;

        // Use camelCase property names (matches repo's configured naming policy)
        var json = $@"{{
  ""correlationId"": ""{correlationId}"",
  ""currentState"": ""Processing"",
  ""pendingServices"": [{(int)ServiceType.Ping}],
  ""completedServices"": [{(int)ServiceType.GeoIP}],
  ""completedAt"": ""{completedAt:O}""
}}";

        h.Seed(key, (RedisValue)json);

        // Act
        var result = await repo.GetByJobIdAsync(jobId);

        // Assert
        result.Should().NotBeNull();

        // These properties are used by GetJobStatus, so they should exist on LookupJobState
        result!.CorrelationId.Should().Be(correlationId);
        result.CurrentState.Should().Be("Processing");
        result.PendingServices.Should().BeEquivalentTo(new[] { ServiceType.Ping });
        result.CompletedServices.Should().BeEquivalentTo(new[] { ServiceType.GeoIP });
        result.CompletedAt.Should().NotBeNull();
        result.CompletedAt!.Value.Should().BeCloseTo(completedAt, TimeSpan.FromSeconds(1));

        h.DbMock.Verify(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString() == key),
                It.IsAny<CommandFlags>()),
            Times.Once);
    }

    private sealed class RedisMoqHarness
    {
        public Mock<IDatabase> DbMock { get; } = new(MockBehavior.Strict);
        public Mock<IConnectionMultiplexer> MuxMock { get; } = new(MockBehavior.Strict);

        public IConnectionMultiplexer Connection => MuxMock.Object;

        private readonly System.Collections.Generic.Dictionary<string, RedisValue> _store = new();

        public RedisMoqHarness()
        {
            // ConnectionMultiplexer.GetDatabase(int db = -1, object? asyncState = null)
            MuxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Returns(DbMock.Object);

            // IDatabase.StringGetAsync(RedisKey key, CommandFlags flags = None)
            DbMock.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) =>
                {
                    var k = key.ToString();
                    return Task.FromResult(_store.TryGetValue(k, out var val) ? val : RedisValue.Null);
                });
        }

        public void Seed(string key, RedisValue value) => _store[key] = value;
    }
}
