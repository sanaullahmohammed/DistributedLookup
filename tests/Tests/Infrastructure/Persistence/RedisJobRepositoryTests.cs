using System;
using System.Linq;
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

public class RedisJobRepositoryTests
{
    [Fact]
    public async Task SaveAsync_ShouldSetValue_WithExpectedKeyPrefix_And24HourExpiry_AndSerializableJson_MetadataOnly()
    {
        // Arrange
        var h = new RedisMoqHarness();
        var repo = new RedisJobRepository(h.Connection);

        var jobId = Guid.NewGuid().ToString();
        var services = new[] { ServiceType.GeoIP, ServiceType.Ping };
        var job = new LookupJob(jobId, "8.8.8.8", LookupTarget.IPAddress, services);

        // Change status so we also prove Status is persisted
        job.MarkAsProcessing();

        // Act
        await repo.SaveAsync(job);

        // Assert (storage key + expiry)
        var expectedKey = $"lookup:job:{jobId}";

        h.MuxMock.Verify(m => m.GetDatabase(It.IsAny<int>(), It.IsAny<object>()), Times.Once);

        h.Store.Should().ContainKey(expectedKey);
        var entry = h.Store[expectedKey];
        entry.Expiry.Should().Be(TimeSpan.FromHours(24));

        // Assert (payload shape/content)
        using var doc = JsonDocument.Parse(entry.Value.ToString());
        var root = doc.RootElement;

        root.GetProperty("JobId").GetString().Should().Be(jobId);
        root.GetProperty("Target").GetString().Should().Be("8.8.8.8");

        ReadEnum<LookupTarget>(root.GetProperty("TargetType"))
            .Should().Be(LookupTarget.IPAddress);

        ReadEnum<JobStatus>(root.GetProperty("Status"))
            .Should().Be(job.Status);

        var createdAt = root.GetProperty("CreatedAt").GetDateTime();
        createdAt.Should().BeCloseTo(job.CreatedAt, TimeSpan.FromSeconds(1));
        createdAt.Kind.Should().Be(DateTimeKind.Utc);

        var completedAtEl = root.GetProperty("CompletedAt");
        completedAtEl.ValueKind.Should().Be(JsonValueKind.Null);

        var requested = root.GetProperty("RequestedServices")
            .EnumerateArray()
            .Select(ReadEnum<ServiceType>)
            .ToArray();

        requested.Should().BeEquivalentTo(services);

        // IMPORTANT: results are NOT stored in job entity and must not be serialized
        root.TryGetProperty("Results", out _).Should().BeFalse();
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

        var createdAt = new DateTime(2025, 01, 02, 03, 04, 05, DateTimeKind.Utc);
        var job = new LookupJob(jobId, "8.8.8.8", LookupTarget.IPAddress, services, createdAt);

        job.MarkAsProcessing();
        job.MarkAsCompleted();
        job.Status.Should().Be(JobStatus.Completed);
        job.CompletedAt.Should().NotBeNull();

        var completedAt = job.CompletedAt;

        await repo.SaveAsync(job);

        // Act
        var loaded = await repo.GetByIdAsync(jobId);

        // Assert
        loaded.Should().NotBeNull();
        loaded!.JobId.Should().Be(jobId);
        loaded.Target.Should().Be("8.8.8.8");
        loaded.TargetType.Should().Be(LookupTarget.IPAddress);

        loaded.Status.Should().Be(JobStatus.Completed);
        loaded.CreatedAt.Should().Be(createdAt);
        loaded.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);

        loaded.CompletedAt.Should().Be(completedAt);
        loaded.CompletedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

        loaded.RequestedServices.Should().BeEquivalentTo(services);
        loaded.IsComplete().Should().BeTrue();
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

    private static TEnum ReadEnum<TEnum>(JsonElement el) where TEnum : struct, Enum
    {
        return el.ValueKind switch
        {
            JsonValueKind.Number => (TEnum)Enum.ToObject(typeof(TEnum), el.GetInt32()),
            JsonValueKind.String => Enum.Parse<TEnum>(el.GetString()!, ignoreCase: true),
            _ => throw new InvalidOperationException($"Cannot parse enum {typeof(TEnum).Name} from JSON kind {el.ValueKind}")
        };
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

            // IDatabase.StringSetAsync(
            //   RedisKey key, RedisValue value, TimeSpan? expiry = null,
            //   bool keepTtl = false, When when = Always, CommandFlags flags = None)
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
