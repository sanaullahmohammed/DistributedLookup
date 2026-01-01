using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Application.Workers;
using DistributedLookup.Contracts;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Application.Workers;

public sealed class LookupWorkerBaseTests
{
    [Fact]
    public async Task Consume_WhenValidateTargetReturnsError_ShouldStoreFailure_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var ct = new CancellationTokenSource().Token;

        var msg = new TestCommand
        {
            JobId = jobId,
            Target = "bad-target",
            TargetType = LookupTarget.Domain
        };

        const string validationError = "Invalid target for this service.";

        var logger = new Mock<ILogger>(MockBehavior.Loose);

        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Strict);

        // Use a mock ResultLocation so we don't depend on concrete implementations
        var storedLocation = new Mock<ResultLocation>().Object;

        TimeSpan savedFailureDuration = default;
        CancellationToken savedFailureCt = default;

        resultStore
            .Setup(s => s.SaveFailureAsync(
                jobId,
                ServiceType.GeoIP,
                validationError,
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, ServiceType, string, TimeSpan, CancellationToken>((_, __, ___, dur, token) =>
            {
                savedFailureDuration = dur;
                savedFailureCt = token;
            })
            .ReturnsAsync(storedLocation);

        TaskCompleted? published = null;
        CancellationToken publishedCt = default;

        var ctx = CreateConsumeContext(msg, ct, (tc, token) =>
        {
            published = tc;
            publishedCt = token;
        });

        var sut = new TestWorker(
            logger.Object,
            resultStore.Object,
            perform: (_, _) => Task.FromResult<object>(new { ok = true }),
            validate: _ => validationError);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - lookup not executed
        sut.ValidateCalls.Should().Be(1);
        sut.PerformCalls.Should().Be(0);

        // Assert - store failure called, store success not called
        resultStore.Verify(s => s.SaveFailureAsync(
                jobId,
                ServiceType.GeoIP,
                validationError,
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        resultStore.Verify(s => s.SaveResultAsync(
                It.IsAny<string>(),
                It.IsAny<ServiceType>(),
                It.IsAny<JsonDocument>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Assert - publish failure event
        published.Should().NotBeNull();
        published!.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.GeoIP);
        published.Success.Should().BeFalse();
        published.ErrorMessage.Should().Be(validationError);
        published.ResultLocation.Should().BeSameAs(storedLocation);

        // Duration should match what was passed to the store (stopwatch is stopped before store call)
        published.Duration.Should().Be(savedFailureDuration);

        // Cancellation token should flow through
        savedFailureCt.Should().Be(ct);
        publishedCt.Should().Be(ct);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
        resultStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Consume_WhenPerformLookupSucceeds_ShouldStoreSuccess_AndPublishTaskCompletedSuccess()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var ct = new CancellationTokenSource().Token;

        var msg = new TestCommand
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var logger = new Mock<ILogger>(MockBehavior.Loose);

        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Strict);

        var storedLocation = new Mock<ResultLocation>().Object;

        string? storedJson = null;
        TimeSpan savedSuccessDuration = default;
        CancellationToken savedSuccessCt = default;

        resultStore
            .Setup(s => s.SaveResultAsync(
                jobId,
                ServiceType.GeoIP,
                It.IsAny<JsonDocument>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, ServiceType, JsonDocument, TimeSpan, CancellationToken>((_, __, doc, dur, token) =>
            {
                storedJson = doc.RootElement.GetRawText();
                savedSuccessDuration = dur;
                savedSuccessCt = token;
            })
            .ReturnsAsync(storedLocation);

        TaskCompleted? published = null;
        CancellationToken publishedCt = default;

        var ctx = CreateConsumeContext(msg, ct, (tc, token) =>
        {
            published = tc;
            publishedCt = token;
        });

        var sut = new TestWorker(
            logger.Object,
            resultStore.Object,
            perform: (_, _) => Task.FromResult<object>(new { answer = 42 }),
            validate: _ => null);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        sut.ValidateCalls.Should().Be(1);
        sut.PerformCalls.Should().Be(1);

        // Store success called
        resultStore.Verify(s => s.SaveResultAsync(
                jobId,
                ServiceType.GeoIP,
                It.IsAny<JsonDocument>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        resultStore.Verify(s => s.SaveFailureAsync(
                It.IsAny<string>(),
                It.IsAny<ServiceType>(),
                It.IsAny<string>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        // Validate stored JSON payload
        storedJson.Should().NotBeNullOrWhiteSpace();
        using (var doc = JsonDocument.Parse(storedJson!))
        {
            doc.RootElement.ValueKind.Should().Be(JsonValueKind.Object);
            doc.RootElement.GetProperty("answer").GetInt32().Should().Be(42);
        }

        // Published event is success and carries location
        published.Should().NotBeNull();
        published!.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.GeoIP);
        published.Success.Should().BeTrue();
        published.ErrorMessage.Should().BeNull();
        published.ResultLocation.Should().BeSameAs(storedLocation);

        // Duration should match what was passed to the store (stopwatch stopped before store call)
        published.Duration.Should().Be(savedSuccessDuration);

        // Cancellation token should flow through
        savedSuccessCt.Should().Be(ct);
        publishedCt.Should().Be(ct);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
        resultStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Consume_WhenPerformLookupThrows_ShouldStoreFailure_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var ct = CancellationToken.None;

        var msg = new TestCommand
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var logger = new Mock<ILogger>(MockBehavior.Loose);

        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Strict);

        const string error = "boom";
        var storedLocation = new Mock<ResultLocation>().Object;

        TimeSpan savedFailureDuration = default;

        resultStore
            .Setup(s => s.SaveFailureAsync(
                jobId,
                ServiceType.GeoIP,
                error,
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .Callback<string, ServiceType, string, TimeSpan, CancellationToken>((_, __, ___, dur, ____) =>
            {
                savedFailureDuration = dur;
            })
            .ReturnsAsync(storedLocation);

        TaskCompleted? published = null;

        var ctx = CreateConsumeContext(msg, ct, (tc, _) => published = tc);

        var sut = new TestWorker(
            logger.Object,
            resultStore.Object,
            perform: (_, _) => throw new InvalidOperationException(error),
            validate: _ => null);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        sut.ValidateCalls.Should().Be(1);
        sut.PerformCalls.Should().Be(1);

        resultStore.Verify(s => s.SaveFailureAsync(
                jobId,
                ServiceType.GeoIP,
                error,
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        resultStore.Verify(s => s.SaveResultAsync(
                It.IsAny<string>(),
                It.IsAny<ServiceType>(),
                It.IsAny<JsonDocument>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.ErrorMessage.Should().Be(error);
        published.ResultLocation.Should().BeSameAs(storedLocation);
        published.Duration.Should().Be(savedFailureDuration);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
        resultStore.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Consume_WhenFailureStoreThrows_ShouldStillPublishTaskCompleted_WithNullResultLocation()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var ct = CancellationToken.None;

        var msg = new TestCommand
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var logger = new Mock<ILogger>(MockBehavior.Loose);

        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Strict);

        const string lookupError = "boom";
        resultStore
            .Setup(s => s.SaveFailureAsync(
                jobId,
                ServiceType.GeoIP,
                lookupError,
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Exception("store down"));

        TaskCompleted? published = null;

        var ctx = CreateConsumeContext(msg, ct, (tc, _) => published = tc);

        var sut = new TestWorker(
            logger.Object,
            resultStore.Object,
            perform: (_, _) => throw new InvalidOperationException(lookupError),
            validate: _ => null);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        published.Should().NotBeNull();
        published!.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.GeoIP);
        published.Success.Should().BeFalse();

        // Error message should remain the lookup exception message (not the store exception)
        published.ErrorMessage.Should().Be(lookupError);

        // Store failed -> ResultLocation stays null, but publish still happens
        published.ResultLocation.Should().BeNull();

        resultStore.Verify(s => s.SaveFailureAsync(
                jobId,
                ServiceType.GeoIP,
                lookupError,
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Once);

        resultStore.Verify(s => s.SaveResultAsync(
                It.IsAny<string>(),
                It.IsAny<ServiceType>(),
                It.IsAny<JsonDocument>(),
                It.IsAny<TimeSpan>(),
                It.IsAny<CancellationToken>()),
            Times.Never);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
        resultStore.VerifyNoOtherCalls();
    }

    private static Mock<ConsumeContext<TestCommand>> CreateConsumeContext(
        TestCommand msg,
        CancellationToken ct,
        Action<TaskCompleted, CancellationToken> onPublish)
    {
        var ctx = new Mock<ConsumeContext<TestCommand>>(MockBehavior.Strict);

        ctx.SetupGet(c => c.Message).Returns(msg);
        ctx.SetupGet(c => c.CancellationToken).Returns(ct);

        ctx.Setup(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()))
            .Callback<TaskCompleted, CancellationToken>((tc, token) => onPublish(tc, token))
            .Returns(Task.CompletedTask);

        return ctx;
    }

    private sealed class TestWorker : LookupWorkerBase<TestCommand>
    {
        private readonly Func<TestCommand, CancellationToken, Task<object>> _perform;
        private readonly Func<TestCommand, string?> _validate;

        public int ValidateCalls { get; private set; }
        public int PerformCalls { get; private set; }

        protected override ServiceType ServiceType => ServiceType.GeoIP;

        public TestWorker(
            ILogger logger,
            IWorkerResultStore resultStore,
            Func<TestCommand, CancellationToken, Task<object>> perform,
            Func<TestCommand, string?> validate)
            : base(logger, resultStore)
        {
            _perform = perform;
            _validate = validate;
        }

        protected override string? ValidateTarget(TestCommand command)
        {
            ValidateCalls++;
            return _validate(command);
        }

        protected override Task<object> PerformLookupAsync(TestCommand command, CancellationToken cancellationToken)
        {
            PerformCalls++;
            return _perform(command, cancellationToken);
        }
    }
}

/// <summary>
/// Must be PUBLIC so Moq/Castle can proxy ConsumeContext&lt;TestCommand&gt; (MassTransit.Abstractions is strong-named).
/// </summary>
public sealed class TestCommand : ILookupCommand
{
    public string JobId { get; set; } = string.Empty;
    public string Target { get; set; } = string.Empty;
    public LookupTarget TargetType { get; set; } = LookupTarget.IPAddress;
}
