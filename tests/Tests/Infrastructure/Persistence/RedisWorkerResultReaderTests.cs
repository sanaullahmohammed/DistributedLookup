using System;
using System.Globalization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Infrastructure.Persistence;
using FluentAssertions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Tests.Infrastructure.Persistence;

public class RedisWorkerResultReaderTests
{
    [Fact]
    public async Task GetResultAsync_WhenKeyMissing_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var sut = new RedisWorkerResultReader(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.GeoIP;

        // Act
        var result = await sut.GetResultAsync(jobId, serviceType, CancellationToken.None);

        // Assert
        result.Should().BeNull();

        h.MuxMock.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Once);

        h.DbMock.Verify(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString() == $"worker-result:{jobId}:{serviceType}"),
                It.Is<CommandFlags>(f => f == CommandFlags.None)),
            Times.Once);

        h.DbMock.VerifyNoOtherCalls();
        h.MuxMock.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetResultAsync_WhenValueIsEmptyString_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var sut = new RedisWorkerResultReader(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.Ping;
        var key = $"worker-result:{jobId}:{serviceType}";

        h.Seed(key, RedisValue.EmptyString);

        // Act
        var result = await sut.GetResultAsync(jobId, serviceType, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetResultAsync_WhenJsonIsInvalid_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var sut = new RedisWorkerResultReader(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.RDAP;
        var key = $"worker-result:{jobId}:{serviceType}";

        h.Seed(key, (RedisValue)"{not json");

        // Act
        var result = await sut.GetResultAsync(jobId, serviceType, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetResultAsync_WhenJsonIsLiteralNull_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var sut = new RedisWorkerResultReader(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.ReverseDNS;
        var key = $"worker-result:{jobId}:{serviceType}";

        h.Seed(key, (RedisValue)"null");

        // Act
        var result = await sut.GetResultAsync(jobId, serviceType, CancellationToken.None);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task GetResultAsync_WhenSuccessPayloadExists_WithData_ShouldReturnWorkerResultData_WithParsedJsonDocument()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var sut = new RedisWorkerResultReader(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.GeoIP;
        var key = $"worker-result:{jobId}:{serviceType}";

        var completedAt = new DateTime(2026, 01, 01, 12, 34, 56, DateTimeKind.Utc);
        var duration = TimeSpan.FromMilliseconds(123);

        // Data written by RedisWorkerResultStore is RootElement.ToString() => JSON text
        var dataJson = @"{""country"":""US"",""asn"":""AS123"",""nested"":{""x"":1}}";

        // NOTE: serviceType is numeric (no JsonStringEnumConverter in reader).
        var storedJson = $@"{{
  ""jobId"": ""{jobId}"",
  ""serviceType"": {(int)serviceType},
  ""success"": true,
  ""errorMessage"": null,
  ""duration"": ""{duration.ToString("c", CultureInfo.InvariantCulture)}"",
  ""completedAt"": ""{completedAt:O}"",
  ""data"": {JsonSerializer.Serialize(dataJson)}
}}";

        h.Seed(key, (RedisValue)storedJson);

        // Act
        var result = await sut.GetResultAsync(jobId, serviceType, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.JobId.Should().Be(jobId);
        result.ServiceType.Should().Be(serviceType);
        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Duration.Should().Be(duration);
        result.CompletedAt.Should().Be(completedAt);

        result.Data.Should().NotBeNull();
        result.Data!.RootElement.GetProperty("country").GetString().Should().Be("US");
        result.Data.RootElement.GetProperty("nested").GetProperty("x").GetInt32().Should().Be(1);
    }

    [Fact]
    public async Task GetResultAsync_WhenFailurePayloadExists_WithNullData_ShouldReturnWorkerResultData_WithNullData()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var sut = new RedisWorkerResultReader(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.Ping;
        var key = $"worker-result:{jobId}:{serviceType}";

        var completedAt = new DateTime(2026, 01, 01, 1, 2, 3, DateTimeKind.Utc);
        var duration = TimeSpan.FromMilliseconds(456);

        // IMPORTANT: serviceType must be numeric (no JsonStringEnumConverter).
        var storedJson = $@"{{
  ""jobId"": ""{jobId}"",
  ""serviceType"": {(int)serviceType},
  ""success"": false,
  ""errorMessage"": ""timeout"",
  ""duration"": ""{duration.ToString("c", CultureInfo.InvariantCulture)}"",
  ""completedAt"": ""{completedAt:O}"",
  ""data"": null
}}";

        h.Seed(key, (RedisValue)storedJson);

        // Act
        var result = await sut.GetResultAsync(jobId, serviceType, CancellationToken.None);

        // Assert
        result.Should().NotBeNull();
        result!.JobId.Should().Be(jobId);
        result.ServiceType.Should().Be(serviceType);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be("timeout");
        result.Duration.Should().Be(duration);
        result.CompletedAt.Should().Be(completedAt);
        result.Data.Should().BeNull();
    }

    [Fact]
    public async Task GetResultAsync_WhenDtoDataIsNonJsonString_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var sut = new RedisWorkerResultReader(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var serviceType = ServiceType.RDAP;
        var key = $"worker-result:{jobId}:{serviceType}";

        var storedJson = $@"{{
  ""jobId"": ""{jobId}"",
  ""serviceType"": {(int)serviceType},
  ""success"": true,
  ""duration"": ""00:00:00.0010000"",
  ""completedAt"": ""2026-01-01T00:00:00.0000000Z"",
  ""data"": ""NOT_JSON""
}}";

        h.Seed(key, (RedisValue)storedJson);

        // Act
        var result = await sut.GetResultAsync(jobId, serviceType, CancellationToken.None);

        // Assert
        // JsonDocument.Parse("NOT_JSON") throws JsonException -> reader returns null
        result.Should().BeNull();
    }

    private sealed class RedisMoqHarness
    {
        public Mock<IDatabase> DbMock { get; } = new(MockBehavior.Strict);
        public Mock<IConnectionMultiplexer> MuxMock { get; } = new(MockBehavior.Strict);

        public IConnectionMultiplexer Connection => MuxMock.Object;

        private readonly System.Collections.Generic.Dictionary<string, RedisValue> _store = new();

        public RedisMoqHarness()
        {
            // IConnectionMultiplexer.GetDatabase(int db = -1, object? asyncState = null)
            MuxMock.Setup(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()))
                .Returns(DbMock.Object);

            // IDatabase.StringGetAsync(RedisKey key, CommandFlags flags = None)
            DbMock.Setup(d => d.StringGetAsync(It.IsAny<RedisKey>(), It.IsAny<CommandFlags>()))
                .Returns((RedisKey key, CommandFlags _) =>
                {
                    var k = key.ToString();
                    return Task.FromResult(_store.TryGetValue(k, out var v) ? v : RedisValue.Null);
                });
        }

        public void Seed(string key, RedisValue value)
        {
            _store[key] = value;
        }
    }
}
