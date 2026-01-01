using System.Net;
using System.Text;
using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using DistributedLookup.Workers.RdapWorker;
using FluentAssertions;
using MassTransit;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Workers.RdapWorker;

// RDAPConsumer has private static cache state; keep these tests non-parallel to avoid cross-test races.
[CollectionDefinition("RDAPConsumer", DisableParallelization = true)]
public class RDAPConsumerCollectionDefinition { }

[Collection("RDAPConsumer")]
public class RDAPConsumerTests
{
    public RDAPConsumerTests()
    {
        ResetBootstrapCache();
    }

    [Fact]
    public async Task Consume_WhenTargetIsIp_AndRdapReturnsSuccess_ShouldSaveSuccess_AndPublishSuccess()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.RDAP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);

        var rawJson = @"{
          ""objectClassName"": ""ip network"",
          ""handle"": ""NET-8-8-8-0-1"",
          ""startAddress"": ""8.8.8.0"",
          ""endAddress"": ""8.8.8.255""
        }";

        var expectedNormalized = NormalizeJson(rawJson);

        var handler = new RoutingHttpMessageHandler(req =>
        {
            req.RequestUri!.ToString().Should().Be("https://rdap.arin.net/registry/ip/8.8.8.8");
            req.Headers.Accept.Any(a => a.MediaType == "application/rdap+json").Should().BeTrue();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rawJson, Encoding.UTF8, "application/rdap+json")
            };
        });

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new RDAPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - repository updated
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        job.Results.Should().ContainKey(ServiceType.RDAP);
        var saved = job.Results[ServiceType.RDAP];
        saved.Success.Should().BeTrue();
        saved.ErrorMessage.Should().BeNull();
        saved.Data.Should().NotBeNull();
        saved.Data!.RootElement.ValueKind.Should().Be(JsonValueKind.String);
        saved.Data.RootElement.GetString().Should().Be(expectedNormalized);

        job.Status.Should().Be(JobStatus.Completed);
        job.CompletedAt.Should().NotBeNull();

        // Assert - publish
        published.Should().NotBeNull();
        published!.JobId.Should().Be(jobId);
        published.ServiceType.Should().Be(ServiceType.RDAP);
        published.Success.Should().BeTrue();
        published.ErrorMessage.Should().BeNull();
        published.Data.Should().Be(expectedNormalized);

        // Ensure it's valid JSON and matches expected payload semantically
        using var doc = JsonDocument.Parse(published.Data!);
        doc.RootElement.GetProperty("objectClassName").GetString().Should().Be("ip network");

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);

        handler.Captured.Should().HaveCount(1);
        handler.Captured[0].Uri.Should().Be("https://rdap.arin.net/registry/ip/8.8.8.8");
        handler.Captured[0].Accept.Should().Contain("application/rdap+json");
    }

    [Fact]
    public async Task Consume_WhenTargetIsIp_AndRdapReturnsNonSuccess_ShouldSaveFailure_AndPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.RDAP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);

        var handler = new RoutingHttpMessageHandler(req =>
            new HttpResponseMessage(HttpStatusCode.NotFound));

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new RDAPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - persisted failure
        job.Results.Should().ContainKey(ServiceType.RDAP);
        var saved = job.Results[ServiceType.RDAP];
        saved.Success.Should().BeFalse();
        saved.Data.Should().BeNull();
        saved.ErrorMessage.Should().Be("RDAP server returned NotFound for '8.8.8.8'");

        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        // Assert - published failure
        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.Data.Should().BeNull();
        published.ErrorMessage.Should().Be("RDAP server returned NotFound for '8.8.8.8'");

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenTargetIsDomain_ShouldFetchIanaBootstrap_UseResolvedBaseUrl_NormalizeToApex_SaveAndPublishSuccess()
    {
        // Arrange
        ResetBootstrapCache();

        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "WWW.Docs.Google.com.",
            TargetType = LookupTarget.Domain
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.RDAP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);

        var bootstrapJson = @"{
          ""services"": [
            [
              [""com"", ""net""],
              [""https://rdap.verisign.com/com/v1""]
            ]
          ]
        }";

        var rdapRaw = @"{
          ""objectClassName"": ""domain"",
          ""handle"": ""EXAMPLE-1"",
          ""ldhName"": ""google.com""
        }";
        var expectedNormalized = NormalizeJson(rdapRaw);

        var handler = new RoutingHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();

            if (uri == "https://data.iana.org/rdap/dns.json")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(bootstrapJson, Encoding.UTF8, "application/json")
                };
            }

            uri.Should().Be("https://rdap.verisign.com/com/v1/domain/google.com");
            req.Headers.Accept.Any(a => a.MediaType == "application/rdap+json").Should().BeTrue();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rdapRaw, Encoding.UTF8, "application/rdap+json")
            };
        });

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new RDAPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        handler.Captured.Should().HaveCount(2);
        handler.Captured[0].Uri.Should().Be("https://data.iana.org/rdap/dns.json");
        handler.Captured[1].Uri.Should().Be("https://rdap.verisign.com/com/v1/domain/google.com");
        handler.Captured[1].Accept.Should().Contain("application/rdap+json");

        job.Results.Should().ContainKey(ServiceType.RDAP);
        job.Results[ServiceType.RDAP].Success.Should().BeTrue();
        job.Results[ServiceType.RDAP].Data!.RootElement.GetString().Should().Be(expectedNormalized);

        published.Should().NotBeNull();
        published!.Success.Should().BeTrue();
        published.Data.Should().Be(expectedNormalized);

        using var doc = JsonDocument.Parse(published.Data!);
        doc.RootElement.GetProperty("ldhName").GetString().Should().Be("google.com");

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenTargetIsDomain_AndBootstrapFails_ShouldFallbackToRdapOrg()
    {
        // Arrange
        ResetBootstrapCache();

        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "www.docs.google.com",
            TargetType = LookupTarget.Domain
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.RDAP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);

        var rdapRaw = @"{ ""objectClassName"": ""domain"", ""ldhName"": ""google.com"" }";
        var expectedNormalized = NormalizeJson(rdapRaw);

        var handler = new RoutingHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();

            if (uri == "https://data.iana.org/rdap/dns.json")
            {
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            uri.Should().Be("https://rdap.org/domain/google.com");
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rdapRaw, Encoding.UTF8, "application/rdap+json")
            };
        });

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new RDAPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        handler.Captured.Should().HaveCount(2);
        handler.Captured[0].Uri.Should().Be("https://data.iana.org/rdap/dns.json");
        handler.Captured[1].Uri.Should().Be("https://rdap.org/domain/google.com");

        job.Results.Should().ContainKey(ServiceType.RDAP);
        job.Results[ServiceType.RDAP].Success.Should().BeTrue();

        published.Should().NotBeNull();
        published!.Success.Should().BeTrue();
        published.Data.Should().Be(expectedNormalized);

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenRdapReturnsInvalidJson_ShouldSaveFailure_AndPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var job = new LookupJob(jobId, msg.Target, msg.TargetType, new[] { ServiceType.RDAP });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>())).ReturnsAsync(job);
        repo.Setup(r => r.SaveAsync(job, It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);

        var handler = new RoutingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not json", Encoding.UTF8, "application/rdap+json")
            });

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new RDAPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        job.Results.Should().ContainKey(ServiceType.RDAP);
        job.Results[ServiceType.RDAP].Success.Should().BeFalse();
        job.Results[ServiceType.RDAP].ErrorMessage.Should().NotBeNullOrWhiteSpace();

        published.Should().NotBeNull();
        published!.Success.Should().BeFalse();
        published.Data.Should().BeNull();
        published.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        published.ErrorMessage!.Should().Contain("json");

        repo.Verify(r => r.SaveAsync(job, It.IsAny<CancellationToken>()), Times.Once);
        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Consume_WhenSuccessButJobNotFound_ShouldNotSave_AndStillPublishSuccess()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJob?)null);

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);

        var raw = @"{ ""objectClassName"": ""ip network"", ""handle"": ""H"" }";
        var expectedNormalized = NormalizeJson(raw);

        var handler = new RoutingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(raw, Encoding.UTF8, "application/rdap+json")
            });

        using var httpClient = new HttpClient(handler);

        TaskCompleted? published = null;
        var ctx = CreateConsumeContext(msg, tc => published = tc);

        var sut = new RDAPConsumer(logger.Object, httpClient, repo.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - no save
        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();

        published.Should().NotBeNull();
        published!.Success.Should().BeTrue();
        published.ServiceType.Should().Be(ServiceType.RDAP);
        published.Data.Should().Be(expectedNormalized);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    // -----------------------
    // Helpers
    // -----------------------

    private static string NormalizeJson(string raw)
    {
        using var doc = JsonDocument.Parse(raw);
        return JsonSerializer.Serialize(doc.RootElement, new JsonSerializerOptions { WriteIndented = false });
    }

    private static Mock<ConsumeContext<CheckRDAP>> CreateConsumeContext(CheckRDAP msg, Action<TaskCompleted> onPublish)
    {
        var ctx = new Mock<ConsumeContext<CheckRDAP>>(MockBehavior.Strict);
        ctx.Setup(x => x.CancellationToken).Returns(CancellationToken.None);

        ctx.SetupGet(c => c.Message).Returns(msg);
        ctx.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        ctx.Setup(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()))
            .Callback<TaskCompleted, CancellationToken>((tc, _) => onPublish(tc))
            .Returns(Task.CompletedTask);

        return ctx;
    }

    private static void ResetBootstrapCache()
    {
        var t = typeof(RDAPConsumer);

        var bootstrapField = t.GetField("_dnsBootstrapByTld",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        bootstrapField?.SetValue(null, null);

        var fetchedAtField = t.GetField("_dnsBootstrapFetchedAt",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        fetchedAtField?.SetValue(null, DateTimeOffset.MinValue);
    }

    private sealed class RoutingHttpMessageHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _router;

        public List<CapturedRequest> Captured { get; } = new();

        public RoutingHttpMessageHandler(Func<HttpRequestMessage, HttpResponseMessage> router)
        {
            _router = router;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Captured.Add(new CapturedRequest(
                request.Method.Method,
                request.RequestUri?.ToString(),
                request.Headers.Accept.ToString()));

            return Task.FromResult(_router(request));
        }
    }

    private sealed record CapturedRequest(string Method, string? Uri, string Accept);
}
