using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Application.Workers;
using DistributedLookup.Contracts.Commands;
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
    public async Task Consume_WhenTargetIsIp_AndRdapReturnsSuccess_ShouldPublishSuccess_AndCallResultStore()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);
        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Loose);

        var rawJson = @"{
          ""objectClassName"": ""ip network"",
          ""handle"": ""NET-8-8-8-0-1"",
          ""startAddress"": ""8.8.8.0"",
          ""endAddress"": ""8.8.8.255""
        }";

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

        var ctx = CreateConsumeContext(msg);

        var sut = new RDAPConsumer(logger.Object, httpClient, resultStore.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - HTTP
        handler.Captured.Should().HaveCount(1);
        handler.Captured[0].Uri.Should().Be("https://rdap.arin.net/registry/ip/8.8.8.8");
        handler.Captured[0].Accept.Should().Contain("application/rdap+json");

        // Assert - store called (don’t assume signature)
        resultStore.Invocations.Should().NotBeEmpty("worker should store a result");

        // Assert - published TaskCompleted (via reflection)
        var published = GetPublishedTaskCompleted(ctx, jobId, ServiceType.RDAP);
        published.Success.Should().BeTrue();
        published.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Consume_WhenTargetIsIp_AndRdapReturnsNonSuccess_ShouldPublishFailure_AndCallResultStore()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);
        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Loose);

        var handler = new RoutingHttpMessageHandler(_ => new HttpResponseMessage(HttpStatusCode.NotFound));
        using var httpClient = new HttpClient(handler);

        var ctx = CreateConsumeContext(msg);

        var sut = new RDAPConsumer(logger.Object, httpClient, resultStore.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - store called
        resultStore.Invocations.Should().NotBeEmpty();

        // Assert - published failure
        var published = GetPublishedTaskCompleted(ctx, jobId, ServiceType.RDAP);
        published.Success.Should().BeFalse();
        published.ErrorMessage.Should().Be("RDAP server returned NotFound for '8.8.8.8'");
    }

    [Fact]
    public async Task Consume_WhenTargetIsDomain_ShouldFetchIanaBootstrap_UseResolvedBaseUrl_NormalizeToApex_AndPublishSuccess()
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

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);
        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Loose);

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
        var ctx = CreateConsumeContext(msg);

        var sut = new RDAPConsumer(logger.Object, httpClient, resultStore.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - HTTP calls
        handler.Captured.Should().HaveCount(2);
        handler.Captured[0].Uri.Should().Be("https://data.iana.org/rdap/dns.json");
        handler.Captured[1].Uri.Should().Be("https://rdap.verisign.com/com/v1/domain/google.com");
        handler.Captured[1].Accept.Should().Contain("application/rdap+json");

        // Assert - published success
        var published = GetPublishedTaskCompleted(ctx, jobId, ServiceType.RDAP);
        published.Success.Should().BeTrue();
        published.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Consume_WhenTargetIsDomain_AndBootstrapFails_ShouldFallbackToRdapOrg_AndPublishSuccess()
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

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);
        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Loose);

        var rdapRaw = @"{ ""objectClassName"": ""domain"", ""ldhName"": ""google.com"" }";

        var handler = new RoutingHttpMessageHandler(req =>
        {
            var uri = req.RequestUri!.ToString();

            if (uri == "https://data.iana.org/rdap/dns.json")
            {
                // Causes EnsureSuccessStatusCode to throw in consumer bootstrap fetch
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            }

            uri.Should().Be("https://rdap.org/domain/google.com");
            req.Headers.Accept.Any(a => a.MediaType == "application/rdap+json").Should().BeTrue();

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(rdapRaw, Encoding.UTF8, "application/rdap+json")
            };
        });

        using var httpClient = new HttpClient(handler);
        var ctx = CreateConsumeContext(msg);

        var sut = new RDAPConsumer(logger.Object, httpClient, resultStore.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - HTTP calls
        handler.Captured.Should().HaveCount(2);
        handler.Captured[0].Uri.Should().Be("https://data.iana.org/rdap/dns.json");
        handler.Captured[1].Uri.Should().Be("https://rdap.org/domain/google.com");

        // Assert - published success
        var published = GetPublishedTaskCompleted(ctx, jobId, ServiceType.RDAP);
        published.Success.Should().BeTrue();
        published.ErrorMessage.Should().BeNull();
    }

    [Fact]
    public async Task Consume_WhenRdapReturnsInvalidJson_ShouldPublishFailure_AndCallResultStore()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckRDAP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var logger = new Mock<ILogger<RDAPConsumer>>(MockBehavior.Loose);
        var resultStore = new Mock<IWorkerResultStore>(MockBehavior.Loose);

        var handler = new RoutingHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("not json", Encoding.UTF8, "application/rdap+json")
            });

        using var httpClient = new HttpClient(handler);
        var ctx = CreateConsumeContext(msg);

        var sut = new RDAPConsumer(logger.Object, httpClient, resultStore.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - store called
        resultStore.Invocations.Should().NotBeEmpty();

        // Assert - published failure
        var published = GetPublishedTaskCompleted(ctx, jobId, ServiceType.RDAP);
        published.Success.Should().BeFalse();
        published.ErrorMessage.Should().NotBeNullOrWhiteSpace();
        published.ErrorMessage!.ToLowerInvariant().Should().Contain("json");
    }

    // -----------------------
    // Helpers
    // -----------------------

    private static Mock<ConsumeContext<CheckRDAP>> CreateConsumeContext(CheckRDAP msg)
    {
        // Loose: base class may call other members.
        var ctx = new Mock<ConsumeContext<CheckRDAP>>(MockBehavior.Loose);
        ctx.SetupGet(c => c.Message).Returns(msg);
        ctx.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);
        return ctx;
    }

    private static void ResetBootstrapCache()
    {
        var t = typeof(RDAPConsumer);

        var bootstrapField = t.GetField("_dnsBootstrapByTld",
            BindingFlags.NonPublic | BindingFlags.Static);
        bootstrapField?.SetValue(null, null);

        var fetchedAtField = t.GetField("_dnsBootstrapFetchedAt",
            BindingFlags.NonPublic | BindingFlags.Static);
        fetchedAtField?.SetValue(null, DateTimeOffset.MinValue);
    }

    private sealed record PublishedTaskCompleted(bool Success, string? ErrorMessage);

    private static PublishedTaskCompleted GetPublishedTaskCompleted(
        Mock<ConsumeContext<CheckRDAP>> ctx,
        string jobId,
        ServiceType serviceType)
    {
        foreach (var inv in ctx.Invocations.Where(i => i.Method.Name.StartsWith("Publish", StringComparison.Ordinal)))
        {
            foreach (var arg in inv.Arguments)
            {
                if (arg == null) continue;

                var t = arg.GetType();
                var jobIdProp = t.GetProperty("JobId");
                var serviceProp = t.GetProperty("ServiceType");
                var successProp = t.GetProperty("Success");

                if (jobIdProp == null || serviceProp == null || successProp == null)
                    continue;

                var j = jobIdProp.GetValue(arg)?.ToString();
                var svcObj = serviceProp.GetValue(arg);
                var okObj = successProp.GetValue(arg);

                if (j != jobId || svcObj is not ServiceType svc || svc != serviceType || okObj is not bool ok)
                    continue;

                var err = t.GetProperty("ErrorMessage")?.GetValue(arg) as string;
                return new PublishedTaskCompleted(ok, err);
            }
        }

        throw new InvalidOperationException("Expected a TaskCompleted publish for the job/service, but none was found.");
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
