using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Workers.PingWorker;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Workers.PingWorker;

public class PingConsumerTests
{
    [Fact]
    public async Task Consume_WhenPingThrows_ShouldPersistFailure_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        var msg = new CheckPing
        {
            JobId = jobId,
            Target = null!, // forces ArgumentNullException in SendPingAsync -> deterministic, no ICMP required
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, "8.8.8.8", LookupTarget.IPAddress, new[] { ServiceType.Ping });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<PingConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new PingConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - repository updated with failure result
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        job.Results.Should().ContainKey(ServiceType.Ping);
        job.Results[ServiceType.Ping].Success.Should().BeFalse();
        job.Results[ServiceType.Ping].Data.Should().BeNull();
        job.Results[ServiceType.Ping].ErrorMessage.Should().NotBeNullOrWhiteSpace();

        // Assert - saga notified
        published.Should().NotBeNull();
        published!.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.Ping);
        published.Success.Should().BeFalse();
        published.Data.Should().BeNull();
        published.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        published.Duration.Should().BeGreaterOrEqualTo(TimeSpan.Zero);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenPingThrows_AndJobNotFound_ShouldNotSave_AndStillPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        var msg = new CheckPing
        {
            JobId = jobId,
            Target = null!, // deterministic exception
            TargetType = LookupTarget.IPAddress
        };

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJob?)null);

        var logger = new Mock<ILogger<PingConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new PingConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - no save, but publish happens
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.ErrorMessage.Should().NotBeNullOrWhiteSpace();

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenPingApiIsAvailable_ShouldPersistSuccess_AndPublishTaskCompletedSuccess()
    {
        // This is effectively an integration-style unit test, because PingConsumer new()'s Ping internally.
        // If the test environment can't do ICMP (common in CI/containers), we skip.
        await EnsurePingAvailableOrSkip();

        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var target = "127.0.0.1";

        var msg = new CheckPing
        {
            JobId = jobId,
            Target = target,
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, target, LookupTarget.IPAddress, new[] { ServiceType.Ping });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<PingConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new PingConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - repository updated with success result
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        job.Results.Should().ContainKey(ServiceType.Ping);
        job.Results[ServiceType.Ping].Success.Should().BeTrue();
        job.Results[ServiceType.Ping].ErrorMessage.Should().BeNull();
        job.Results[ServiceType.Ping].Data.Should().NotBeNull();

        // Assert - published success event contains JSON payload
        published.Should().NotBeNull();
        published!.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.Ping);
        published.Success.Should().BeTrue();
        published.ErrorMessage.Should().BeNull();
        published.Data.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(published.Data!);

        GetJsonInt32CaseInsensitive(doc.RootElement, "PacketsSent").Should().Be(4);
        GetJsonStringCaseInsensitive(doc.RootElement, "Target").Should().Be(target);

        // Ensure Results array exists and has 4 entries
        var resultsEl = GetJsonElementCaseInsensitive(doc.RootElement, "Results");
        resultsEl.ValueKind.Should().Be(JsonValueKind.Array);
        resultsEl.GetArrayLength().Should().Be(4);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenPingApiIsAvailable_AndJobNotFound_ShouldNotSave_ButStillPublishTaskCompletedSuccess()
    {
        await EnsurePingAvailableOrSkip();

        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var target = "127.0.0.1";

        var msg = new CheckPing
        {
            JobId = jobId,
            Target = target,
            TargetType = LookupTarget.IPAddress
        };

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJob?)null);

        var logger = new Mock<ILogger<PingConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new PingConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - no save since job missing
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        published.Should().NotBeNull();
        published!.Success.Should().BeTrue();
        published.ErrorMessage.Should().BeNull();
        published.Data.Should().NotBeNullOrWhiteSpace();

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static async Task EnsurePingAvailableOrSkip()
    {
        try
        {
            using var ping = new System.Net.NetworkInformation.Ping();
            _ = await ping.SendPingAsync("127.0.0.1", 1000);
        }
        catch (Exception ex)
        {
            throw Xunit.Sdk.SkipException.ForSkip(
    $"ICMP Ping not available in this test environment: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static Mock<ConsumeContext<CheckPing>> CreateConsumeContext(CheckPing msg, Action<TaskCompleted> onPublish)
    {
        var ctx = new Mock<ConsumeContext<CheckPing>>(MockBehavior.Strict);
        ctx.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        ctx.SetupGet(c => c.Message).Returns(msg);

        ctx.Setup(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()))
            .Callback<TaskCompleted, CancellationToken>((tc, _) => onPublish(tc))
            .Returns(Task.CompletedTask);

        return ctx;
    }

    private static string? GetJsonStringCaseInsensitive(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var exact) && exact.ValueKind == JsonValueKind.String)
            return exact.GetString();

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
                return prop.Value.GetString();
        }

        return null;
    }

    private static int GetJsonInt32CaseInsensitive(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var exact) && exact.ValueKind == JsonValueKind.Number)
            return exact.GetInt32();

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.Number)
                return prop.Value.GetInt32();
        }

        throw new InvalidOperationException($"Property '{propertyName}' not found or not a number.");
    }

    private static JsonElement GetJsonElementCaseInsensitive(JsonElement root, string propertyName)
    {
        if (root.TryGetProperty(propertyName, out var exact))
            return exact;

        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                return prop.Value;
        }

        throw new InvalidOperationException($"Property '{propertyName}' not found.");
    }
}
