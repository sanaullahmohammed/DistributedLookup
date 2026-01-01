using System.Net;
using System.Text;
using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Workers.GeoWorker;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Workers.GeoWorker;

public class GeoIPConsumerTests
{
    [Fact]
    public async Task Consume_WhenApiReturnsSuccess_ShouldSaveSuccessResult_AndPublishTaskCompletedSuccess()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.GeoIP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var json = SuccessGeoJson();
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new GeoIPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - HTTP call happened and used expected endpoint
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri.Should().NotBeNull();
        handler.LastRequest!.RequestUri!.ToString().Should().Contain("http://ip-api.com/json/");
        handler.LastRequest!.RequestUri!.ToString().Should().Contain("/json/8.8.8.8");
        handler.LastRequest!.RequestUri!.Query.Should().Contain("fields=status,message,country,countryCode,region,regionName,city,zip,lat,lon,timezone,isp,org,as");

        // Assert - repository interaction
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        // Assert - job updated
        job.Results.Should().ContainKey(ServiceType.GeoIP);
        var result = job.Results[ServiceType.GeoIP];
        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Data.Should().NotBeNull();
        job.Status.Should().Be(JobStatus.Completed);
        job.CompletedAt.Should().NotBeNull();

        // Assert - saga notification published
        published.Should().NotBeNull();
        published!.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.GeoIP);
        published.Success.Should().BeTrue();
        published.ErrorMessage.Should().BeNull();
        published.Duration.Should().BeGreaterOrEqualTo(TimeSpan.Zero);
        published.Data.Should().NotBeNullOrWhiteSpace();

        // Data is JSON string of the response object
        using var doc = JsonDocument.Parse(published.Data!);
        GetJsonStringCaseInsensitive(doc.RootElement, "Status").Should().Be("success");

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenApiReturnsNonSuccessStatusCode_ShouldSaveFailure_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.GeoIP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new GeoIPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - repository updated with failure
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        job.Results.Should().ContainKey(ServiceType.GeoIP);
        var result = job.Results[ServiceType.GeoIP];
        result.Success.Should().BeFalse();
        result.Data.Should().BeNull();
        result.ErrorMessage.Should().Be("GeoIP API returned InternalServerError");

        // Assert - published failure event
        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.ErrorMessage.Should().Be("GeoIP API returned InternalServerError");
        published.Data.Should().BeNull();
        published.Duration.Should().BeGreaterOrEqualTo(TimeSpan.Zero);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenApiReturnsFailStatusInJson_ShouldSaveFailureWithMessage_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.GeoIP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var json = @"{""status"":""fail"",""message"":""invalid query""}";
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new GeoIPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        job.Results.Should().ContainKey(ServiceType.GeoIP);
        job.Results[ServiceType.GeoIP].Success.Should().BeFalse();
        job.Results[ServiceType.GeoIP].ErrorMessage.Should().Be("invalid query");

        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.ErrorMessage.Should().Be("invalid query");

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenHttpClientThrows_ShouldSaveFailureWithExceptionMessage_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.GeoIP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("boom"));
        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new GeoIPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        job.Results.Should().ContainKey(ServiceType.GeoIP);
        job.Results[ServiceType.GeoIP].Success.Should().BeFalse();
        job.Results[ServiceType.GeoIP].ErrorMessage.Should().Contain("boom");

        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.ErrorMessage.Should().Contain("boom");
        published.Data.Should().BeNull();

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenJobNotFound_ShouldNotSave_AndStillPublishTaskCompletedSuccess()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJob?)null);

        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(SuccessGeoJson(), Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new GeoIPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - no save since no job
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        published.Should().NotBeNull();
        published!.Success.Should().BeTrue();
        published.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.GeoIP);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    private static Mock<ConsumeContext<CheckGeoIP>> CreateConsumeContext(CheckGeoIP msg, Action<TaskCompleted> onPublish)
    {
        var ctx = new Mock<ConsumeContext<CheckGeoIP>>(MockBehavior.Strict);
        ctx.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        ctx.SetupGet(c => c.Message).Returns(msg);

        ctx.Setup(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()))
            .Callback<TaskCompleted, CancellationToken>((tc, _) => onPublish(tc))
            .Returns(Task.CompletedTask);

        return ctx;
    }

    private static string SuccessGeoJson() =>
        // Minimal-but-sufficient for GeoIPResponse deserialization + status == "success"
        @"{
            ""status"": ""success"",
            ""country"": ""United States"",
            ""countryCode"": ""US"",
            ""region"": ""CA"",
            ""regionName"": ""California"",
            ""city"": ""Mountain View"",
            ""zip"": ""94043"",
            ""lat"": 37.4,
            ""lon"": -122.1,
            ""timezone"": ""America/Los_Angeles"",
            ""isp"": ""TestISP"",
            ""org"": ""TestOrg"",
            ""as"": ""AS123""
          }";

    private static string? GetJsonStringCaseInsensitive(JsonElement root, string propertyName)
    {
        // Try exact first
        if (root.TryGetProperty(propertyName, out var exact) && exact.ValueKind == JsonValueKind.String)
            return exact.GetString();

        // Fall back to case-insensitive scan
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, propertyName, StringComparison.OrdinalIgnoreCase) &&
                prop.Value.ValueKind == JsonValueKind.String)
            {
                return prop.Value.GetString();
            }
        }

        return null;
    }

    private sealed class StubHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _handler;

        public HttpRequestMessage? LastRequest { get; private set; }

        public StubHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> handler)
        {
            _handler = handler;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(_handler(request));
        }
    }
}
