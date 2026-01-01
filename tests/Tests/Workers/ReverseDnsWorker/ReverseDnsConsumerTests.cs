using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Workers.ReverseDnsWorker;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Workers.ReverseDnsWorker;

public class ReverseDnsConsumerTests
{
    [Fact]
    public async Task Consume_WhenTargetTypeIsNotIp_ShouldPersistFailure_AndPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = "example.com",
            TargetType = LookupTarget.Domain
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.ReverseDNS });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, CancellationToken.None, tc => published = tc);

        var sut = new ReverseDnsConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - persisted failure
        job.Results.Should().ContainKey(ServiceType.ReverseDNS);
        job.Results[ServiceType.ReverseDNS].Success.Should().BeFalse();
        job.Results[ServiceType.ReverseDNS].Data.Should().BeNull();
        job.Results[ServiceType.ReverseDNS].ErrorMessage.Should().Be("Reverse DNS lookup requires an IP address target.");

        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        // Assert - published failure
        published.Should().NotBeNull();
        published!.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.ReverseDNS);
        published.Success.Should().BeFalse();
        published.Data.Should().BeNull();
        published.ErrorMessage.Should().Be("Reverse DNS lookup requires an IP address target.");

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenTargetIsNotValidIp_ShouldPersistFailure_AndPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = "not-an-ip",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.ReverseDNS });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, CancellationToken.None, tc => published = tc);

        var sut = new ReverseDnsConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        job.Results.Should().ContainKey(ServiceType.ReverseDNS);
        job.Results[ServiceType.ReverseDNS].Success.Should().BeFalse();
        job.Results[ServiceType.ReverseDNS].Data.Should().BeNull();
        job.Results[ServiceType.ReverseDNS].ErrorMessage.Should().Be("Invalid IP address: not-an-ip");

        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.ErrorMessage.Should().Be("Invalid IP address: not-an-ip");

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenDnsLookupTimesOut_ShouldPersistFailure_AndPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.ReverseDNS });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        // Cancellation is only used for Task.Delay(timeout, ct). If ct is already canceled,
        // Delay completes immediately and the code takes the "timed out" branch deterministically.
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, cts.Token, tc => published = tc);

        var sut = new ReverseDnsConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        job.Results.Should().ContainKey(ServiceType.ReverseDNS);
        job.Results[ServiceType.ReverseDNS].Success.Should().BeFalse();
        job.Results[ServiceType.ReverseDNS].ErrorMessage.Should().Be("Reverse DNS lookup timed out after 5s.");

        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.ErrorMessage.Should().Be("Reverse DNS lookup timed out after 5s.");

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenPtrRecordExists_ShouldPersistSuccess_AndPublishSuccess()
    {
        // This is environment-dependent (uses OS resolver). We probe first and skip if unsupported.
        await EnsureReverseDnsWorksOrSkip(IPAddress.Loopback, timeoutMs: 1000);

        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var target = "127.0.0.1";

        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = target,
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, target, LookupTarget.IPAddress, new[] { ServiceType.ReverseDNS });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, CancellationToken.None, tc => published = tc);

        var sut = new ReverseDnsConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - persisted success
        job.Results.Should().ContainKey(ServiceType.ReverseDNS);
        var saved = job.Results[ServiceType.ReverseDNS];
        saved.Success.Should().BeTrue();
        saved.ErrorMessage.Should().BeNull();
        saved.Data.Should().NotBeNull();

        // SaveResult passes a JSON string into CreateSuccess => stored as JSON string value
        var raw = saved.Data!.RootElement.GetString();
        raw.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(raw!);
        doc.RootElement.GetProperty("Input").GetString().Should().Be(target);
        doc.RootElement.GetProperty("Found").GetBoolean().Should().BeTrue();
        doc.RootElement.TryGetProperty("HostName", out var hostEl).Should().BeTrue();
        hostEl.ValueKind.Should().Be(JsonValueKind.String);
        hostEl.GetString().Should().NotBeNullOrWhiteSpace();

        // Assert - published success
        published.Should().NotBeNull();
        published!.Success.Should().BeTrue();
        published.ServiceType.Should().Be(ServiceType.ReverseDNS);
        published.Data.Should().Be(raw);
        published.ErrorMessage.Should().BeNull();

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenNoPtrRecord_CommonSocketException_ShouldPersistSuccessFoundFalse_AndPublishSuccess()
    {
        // Try an address from TEST-NET-1; skip if environment behaves differently.
        var ip = IPAddress.Parse("192.0.2.1");
        await EnsureNoPtrOrSkip(ip, timeoutMs: 1500);

        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var target = ip.ToString();

        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = target,
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, target, LookupTarget.IPAddress, new[] { ServiceType.ReverseDNS });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, CancellationToken.None, tc => published = tc);

        var sut = new ReverseDnsConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - this branch is considered "Success=true" with Found=false
        job.Results.Should().ContainKey(ServiceType.ReverseDNS);
        var saved = job.Results[ServiceType.ReverseDNS];
        saved.Success.Should().BeTrue();
        saved.ErrorMessage.Should().BeNull();
        saved.Data.Should().NotBeNull();

        var raw = saved.Data!.RootElement.GetString();
        raw.Should().NotBeNullOrWhiteSpace();

        using var doc = JsonDocument.Parse(raw!);
        doc.RootElement.GetProperty("Input").GetString().Should().Be(target);
        doc.RootElement.GetProperty("Found").GetBoolean().Should().BeFalse();
        doc.RootElement.GetProperty("HostName").ValueKind.Should().Be(JsonValueKind.Null);

        published.Should().NotBeNull();
        published!.Success.Should().BeTrue();
        published.Data.Should().Be(raw);

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenJobNotFound_OnFailure_ShouldNotSave_ButStillPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = "example.com",
            TargetType = LookupTarget.Domain
        };

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJob?)null);

        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, CancellationToken.None, tc => published = tc);

        var sut = new ReverseDnsConsumer(logger.Object, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.ErrorMessage.Should().Be("Reverse DNS lookup requires an IP address target.");

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -----------------------
    // Helpers
    // -----------------------

    private static Mock<ConsumeContext<CheckReverseDNS>> CreateConsumeContext(
        CheckReverseDNS msg,
        CancellationToken ct,
        Action<TaskCompleted> onPublish)
    {
        var ctx = new Mock<ConsumeContext<CheckReverseDNS>>(MockBehavior.Strict);
        ctx.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        ctx.SetupGet(c => c.Message).Returns(msg);
        ctx.SetupGet(c => c.CancellationToken).Returns(ct);

        ctx.Setup(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()))
            .Callback<TaskCompleted, CancellationToken>((tc, _) => onPublish(tc))
            .Returns(Task.CompletedTask);

        return ctx;
    }

    private static async Task EnsureReverseDnsWorksOrSkip(IPAddress ip, int timeoutMs)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            var completed = await Task.WhenAny(lookup, Task.Delay(timeoutMs));

            if (completed != lookup)
                throw Xunit.Sdk.SkipException.ForSkip($"Reverse DNS probe timed out after {timeoutMs}ms in this environment.");

            var entry = await lookup;
            if (string.IsNullOrWhiteSpace(entry.HostName))
                throw Xunit.Sdk.SkipException.ForSkip("Reverse DNS probe returned an empty hostname in this environment.");
        }
        catch (Exception ex)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"Reverse DNS not available in this environment: {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static async Task EnsureNoPtrOrSkip(IPAddress ip, int timeoutMs)
    {
        try
        {
            var lookup = Dns.GetHostEntryAsync(ip);
            var completed = await Task.WhenAny(lookup, Task.Delay(timeoutMs));

            if (completed != lookup)
                throw Xunit.Sdk.SkipException.ForSkip($"No-PTR probe timed out after {timeoutMs}ms in this environment.");

            try
            {
                _ = await lookup;
                throw Xunit.Sdk.SkipException.ForSkip("No-PTR probe unexpectedly resolved a hostname in this environment.");
            }
            catch (SocketException se) when (se.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
            {
                // Expected: no PTR record
            }
            catch (Exception ex)
            {
                throw Xunit.Sdk.SkipException.ForSkip($"No-PTR probe produced an unexpected exception: {ex.GetType().Name}: {ex.Message}");
            }
        }
        catch (Exception ex)
        {
            throw Xunit.Sdk.SkipException.ForSkip($"No-PTR probe not available in this environment: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
