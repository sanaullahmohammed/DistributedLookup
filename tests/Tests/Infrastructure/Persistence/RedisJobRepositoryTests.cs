using System.Text.Json;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Infrastructure.Persistence;
using FluentAssertions;
using Moq;
using StackExchange.Redis;
using Xunit;

namespace Tests.Infrastructure.Persistence;

public class RedisJobRepositoryTests
{
    [Fact]
    public async Task SaveAsync_ShouldSetValue_WithExpectedKeyPrefix_And24HourExpiry_AndSerializableJson()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisJobRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var services = new[] { ServiceType.GeoIP, ServiceType.Ping };
        var job = new LookupJob(jobId, "8.8.8.8", LookupTarget.IPAddress, services);

        var geoResult = ServiceResult.CreateSuccess(
            ServiceType.GeoIP,
            new { country = "US" },
            TimeSpan.FromMilliseconds(12));

        job.AddResult(ServiceType.GeoIP, geoResult);

        // Act
        await repo.SaveAsync(job);

        // Assert (call verification)
        var expectedKey = $"lookup:job:{jobId}";

        h.MuxMock.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Once);

        h.DbMock.Verify(d => d.StringSetAsync(
                It.Is<RedisKey>(k => k.ToString() == expectedKey),
                It.IsAny<RedisValue>(),
                It.Is<TimeSpan?>(t => t == TimeSpan.FromHours(24)),
                It.Is<bool>(keepTtl => keepTtl == false),
                It.Is<When>(w => w == When.Always),
                It.Is<CommandFlags>(f => f == CommandFlags.None)),
            Times.Once);

        // Assert (storage & payload)
        h.Store.Should().ContainKey(expectedKey);

        var entry = h.Store[expectedKey];
        entry.Expiry.Should().Be(TimeSpan.FromHours(24));

        using var doc = JsonDocument.Parse(entry.Value.ToString());
        var root = doc.RootElement;

        root.GetProperty("JobId").GetString().Should().Be(jobId);
        root.GetProperty("Target").GetString().Should().Be("8.8.8.8");
        root.GetProperty("TargetType").GetInt32().Should().Be((int)LookupTarget.IPAddress);
        root.GetProperty("Status").GetInt32().Should().Be((int)JobStatus.Pending);

        // CreatedAt comes from the domain entity
        var createdAt = root.GetProperty("CreatedAt").GetDateTime();
        createdAt.Should().BeCloseTo(job.CreatedAt, TimeSpan.FromSeconds(1));

        root.GetProperty("CompletedAt").ValueKind.Should().Be(JsonValueKind.Null);

        var requested = root.GetProperty("RequestedServices").EnumerateArray().Select(e => e.GetInt32()).ToArray();
        requested.Should().BeEquivalentTo(services.Select(s => (int)s));

        var results = root.GetProperty("Results").EnumerateArray().ToArray();
        results.Should().HaveCount(1);

        var r = results.Single();
        r.GetProperty("ServiceType").GetInt32().Should().Be((int)ServiceType.GeoIP);
        r.GetProperty("Success").GetBoolean().Should().BeTrue();
        r.GetProperty("ErrorMessage").ValueKind.Should().Be(JsonValueKind.Null);
        r.GetProperty("Data").GetString().Should().Be(geoResult.Data!.RootElement.ToString());
        r.GetProperty("DurationMs").GetInt64().Should().Be(12);
    }

    [Fact]
    public async Task GetByIdAsync_WhenKeyDoesNotExist_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisJobRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();

        // Act
        var result = await repo.GetByIdAsync(jobId);

        // Assert
        result.Should().BeNull();

        h.DbMock.Verify(d => d.StringGetAsync(
                It.Is<RedisKey>(k => k.ToString() == $"lookup:job:{jobId}"),
                It.Is<CommandFlags>(f => f == CommandFlags.None)),
            Times.Once);
    }

    [Fact]
    public async Task GetByIdAsync_WhenValueIsEmptyString_ShouldReturnNull()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisJobRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var key = $"lookup:job:{jobId}";

        h.Seed(key, RedisValue.EmptyString, TimeSpan.FromHours(1));

        // Act
        var result = await repo.GetByIdAsync(jobId);

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public async Task SaveAsync_ThenGetByIdAsync_ShouldRoundTrip_StableFields()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisJobRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var services = new[] { ServiceType.GeoIP, ServiceType.Ping };
        var job = new LookupJob(jobId, "8.8.8.8", LookupTarget.IPAddress, services);

        var geoDuration = TimeSpan.FromMilliseconds(100);
        var pingDuration = TimeSpan.FromMilliseconds(200);

        var geoOk = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { x = 1 }, geoDuration);
        var pingFail = ServiceResult.CreateFailure(ServiceType.Ping, "No response", pingDuration);

        job.AddResult(ServiceType.GeoIP, geoOk);
        job.AddResult(ServiceType.Ping, pingFail);

        job.Status.Should().Be(JobStatus.Completed);
        job.CompletedAt.Should().NotBeNull();

        var completedAt = job.CompletedAt;

        await repo.SaveAsync(job);

        var beforeGet = DateTime.UtcNow;

        // Act
        var loaded = await repo.GetByIdAsync(jobId);

        var afterGet = DateTime.UtcNow;

        // Assert
        loaded.Should().NotBeNull();
        loaded!.JobId.Should().Be(jobId);
        loaded.Target.Should().Be("8.8.8.8");
        loaded.TargetType.Should().Be(LookupTarget.IPAddress);

        // These are restored via reflection
        loaded.Status.Should().Be(JobStatus.Completed);
        loaded.CompletedAt.Should().Be(completedAt);

        loaded.RequestedServices.Should().BeEquivalentTo(services);

        // NOTE: CreatedAt is NOT restored; constructor sets it at deserialization time
        loaded.CreatedAt.Should().BeOnOrAfter(job.CreatedAt);

        loaded.IsComplete().Should().BeTrue();
        loaded.CompletionPercentage().Should().Be(100);

        loaded.Results.Should().HaveCount(2);

        var loadedGeo = loaded.Results[ServiceType.GeoIP];
        loadedGeo.Success.Should().BeTrue();
        loadedGeo.ErrorMessage.Should().BeNull();
        loadedGeo.Duration.Should().Be(geoDuration);

        // DeserializeJob passes a string into CreateSuccess(...), so the JsonDocument becomes a JSON string
        loadedGeo.Data.Should().NotBeNull();
        loadedGeo.Data!.RootElement.ValueKind.Should().Be(JsonValueKind.String);
        loadedGeo.Data!.RootElement.GetString().Should().Be(geoOk.Data!.RootElement.ToString());

        // CompletedAt for results is NOT preserved by DeserializeJob (it uses DateTime.UtcNow)
        loadedGeo.CompletedAt.Should().BeOnOrAfter(beforeGet).And.BeOnOrBefore(afterGet);

        var loadedPing = loaded.Results[ServiceType.Ping];
        loadedPing.Success.Should().BeFalse();
        loadedPing.ErrorMessage.Should().Be("No response");
        loadedPing.Duration.Should().Be(pingDuration);
        loadedPing.Data.Should().BeNull();
        loadedPing.CompletedAt.Should().BeOnOrAfter(beforeGet).And.BeOnOrBefore(afterGet);
    }

    [Fact]
    public async Task GetByIdAsync_WhenJsonLiteralNull_ShouldThrowInvalidOperationException()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisJobRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var key = $"lookup:job:{jobId}";

        h.Seed(key, (RedisValue)"null", TimeSpan.FromHours(1));

        // Act
        Func<Task> act = async () => await repo.GetByIdAsync(jobId);

        // Assert
        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage("Failed to deserialize job");
    }

    [Fact]
    public async Task GetPendingJobsAsync_ShouldReturnEmpty_AndNotTouchRedis()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisJobRepository(h.Connection);

        // Act
        var pending = await repo.GetPendingJobsAsync();

        // Assert
        pending.Should().BeEmpty();
        h.MuxMock.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Never);
    }

    private sealed class RedisMoqHarness
    {
        public Dictionary<string, (RedisValue Value, TimeSpan? Expiry)> Store { get; } = new();

        public Mock<IDatabase> DbMock { get; } = new(MockBehavior.Strict);
        public Mock<IConnectionMultiplexer> MuxMock { get; } = new(MockBehavior.Strict);

        public IConnectionMultiplexer Connection => MuxMock.Object;

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
                    var value = Store.TryGetValue(k, out var entry) ? entry.Value : RedisValue.Null;
                    return Task.FromResult(value);
                });

            // IDatabase.StringSetAsync(RedisKey key, RedisValue value, TimeSpan? expiry = null,
            //                         bool keepTtl = false, When when = Always, CommandFlags flags = None)
            DbMock.Setup(d => d.StringSetAsync(
                    It.IsAny<RedisKey>(),
                    It.IsAny<RedisValue>(),
                    It.IsAny<TimeSpan?>(),
                    It.IsAny<bool>(),
                    It.IsAny<When>(),
                    It.IsAny<CommandFlags>()))
                .Callback<RedisKey, RedisValue, TimeSpan?, bool, When, CommandFlags>((key, value, expiry, _, __, ___) =>
                {
                    Store[key.ToString()] = (value, expiry);
                })
                .Returns(Task.FromResult(true));
        }

        public void Seed(string key, RedisValue value, TimeSpan? expiry = null)
        {
            Store[key] = (value, expiry);
        }
    }
}
