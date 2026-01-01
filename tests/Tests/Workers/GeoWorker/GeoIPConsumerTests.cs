using System;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Application.Workers;
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
    public async Task Consume_WhenApiReturnsSuccess_ShouldStoreSuccessResult_AndPublishTaskCompletedSuccess()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var json = SuccessGeoJson();
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(msg, p =>
        {
            published = p;
            publishCount++;
        });

        var sut = new GeoIPConsumer(logger.Object, httpClient, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - HTTP call happened and used expected endpoint
        handler.LastRequest.Should().NotBeNull();
        handler.LastRequest!.RequestUri.Should().NotBeNull();

        var uri = handler.LastRequest!.RequestUri!.ToString();
        uri.Should().Contain("http://ip-api.com/json/");
        uri.Should().Contain("/json/8.8.8.8");
        uri.Should().Contain("fields=status,message,country,countryCode,region,regionName,city,zip,lat,lon,timezone,isp,org,as");

        // Assert - result stored
        AssertResultStoreCalled(store, jobId, ServiceType.GeoIP);

        var storedSuccess = TryExtractStoredSuccess(store, jobId, ServiceType.GeoIP);
        if (storedSuccess is not null)
            storedSuccess.Value.Should().BeTrue();

        var storedError = TryExtractStoredErrorMessage(store, jobId, ServiceType.GeoIP);
        if (storedError is not null)
            storedError.Should().BeNullOrWhiteSpace();

        // If we can extract stored JSON, validate it
        var storedJson = TryExtractStoredJson(store, jobId, ServiceType.GeoIP);
        if (!string.IsNullOrWhiteSpace(storedJson))
        {
            using var doc = JsonDocument.Parse(storedJson!);
            GetJsonStringCaseInsensitive(doc.RootElement, "status").Should().Be("success");
        }

        // Assert - saga notification published
        publishCount.Should().Be(1);
        published.Should().NotBeNull();

        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.GeoIP,
            expectedSuccess: true);

        ctx.Verify(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    [Fact]
    public async Task Consume_WhenApiReturnsNonSuccessStatusCode_ShouldStoreFailure_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.InternalServerError));

        using var httpClient = new HttpClient(handler);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(msg, p =>
        {
            published = p;
            publishCount++;
        });

        var sut = new GeoIPConsumer(logger.Object, httpClient, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - stored failure
        AssertResultStoreCalled(store, jobId, ServiceType.GeoIP);

        var storedSuccess = TryExtractStoredSuccess(store, jobId, ServiceType.GeoIP);
        if (storedSuccess is not null)
            storedSuccess.Value.Should().BeFalse();

        var storedError = TryExtractStoredErrorMessage(store, jobId, ServiceType.GeoIP);
        if (storedError is not null)
            storedError.Should().Be("GeoIP API returned InternalServerError");

        // Assert - published failure
        publishCount.Should().Be(1);
        published.Should().NotBeNull();

        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.GeoIP,
            expectedSuccess: false,
            expectedErrorEquals: "GeoIP API returned InternalServerError");
    }

    [Fact]
    public async Task Consume_WhenApiReturnsFailStatusInJson_ShouldStoreFailureWithMessage_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var json = @"{""status"":""fail"",""message"":""invalid query""}";
        var handler = new StubHttpMessageHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            });

        using var httpClient = new HttpClient(handler);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(msg, p =>
        {
            published = p;
            publishCount++;
        });

        var sut = new GeoIPConsumer(logger.Object, httpClient, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - stored failure
        AssertResultStoreCalled(store, jobId, ServiceType.GeoIP);

        var storedSuccess = TryExtractStoredSuccess(store, jobId, ServiceType.GeoIP);
        if (storedSuccess is not null)
            storedSuccess.Value.Should().BeFalse();

        var storedError = TryExtractStoredErrorMessage(store, jobId, ServiceType.GeoIP);
        if (storedError is not null)
            storedError.Should().Be("invalid query");

        // Assert - published failure
        publishCount.Should().Be(1);
        published.Should().NotBeNull();

        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.GeoIP,
            expectedSuccess: false,
            expectedErrorEquals: "invalid query");
    }

    [Fact]
    public async Task Consume_WhenHttpClientThrows_ShouldStoreFailureWithExceptionMessage_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckGeoIP
        {
            JobId = jobId,
            Target = "8.8.8.8",
            TargetType = LookupTarget.IPAddress
        };

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<GeoIPConsumer>>(MockBehavior.Loose);

        var handler = new StubHttpMessageHandler(_ => throw new HttpRequestException("boom"));
        using var httpClient = new HttpClient(handler);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(msg, p =>
        {
            published = p;
            publishCount++;
        });

        var sut = new GeoIPConsumer(logger.Object, httpClient, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - stored failure
        AssertResultStoreCalled(store, jobId, ServiceType.GeoIP);

        var storedSuccess = TryExtractStoredSuccess(store, jobId, ServiceType.GeoIP);
        if (storedSuccess is not null)
            storedSuccess.Value.Should().BeFalse();

        var storedError = TryExtractStoredErrorMessage(store, jobId, ServiceType.GeoIP);
        if (storedError is not null)
            storedError.Should().Contain("boom");

        // Assert - published failure
        publishCount.Should().Be(1);
        published.Should().NotBeNull();

        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.GeoIP,
            expectedSuccess: false,
            expectedErrorContains: "boom");
    }

    // -----------------------
    // ConsumeContext + publish capture
    // -----------------------

    private static Mock<ConsumeContext<CheckGeoIP>> CreateConsumeContext(CheckGeoIP msg, Action<object> onPublish)
    {
        var ctx = new Mock<ConsumeContext<CheckGeoIP>>(MockBehavior.Loose)
        {
            DefaultValueProvider = new TaskFriendlyDefaultValueProvider()
        };

        ctx.SetupGet(c => c.Message).Returns(msg);
        ctx.SetupGet(c => c.CancellationToken).Returns(CancellationToken.None);

        // Publish overload: Publish(TaskCompleted message, CancellationToken)
        ctx.Setup(c => c.Publish(It.IsAny<TaskCompleted>(), It.IsAny<CancellationToken>()))
            .Callback<TaskCompleted, CancellationToken>((tc, _) => onPublish(tc))
            .Returns(Task.CompletedTask);

        // Publish overload: Publish<TaskCompleted>(object values, CancellationToken)
        ctx.Setup(c => c.Publish<TaskCompleted>(It.IsAny<object>(), It.IsAny<CancellationToken>()))
            .Callback<object, CancellationToken>((values, _) => onPublish(values))
            .Returns(Task.CompletedTask);

        return ctx;
    }

    // -----------------------
    // Assertions: store + publish (reflection-based, resilient to DTO changes)
    // -----------------------

    private static void AssertResultStoreCalled(Mock<IWorkerResultStore> store, string jobId, ServiceType serviceType)
    {
        store.Invocations.Should().NotBeEmpty("worker should persist a result via IWorkerResultStore");

        var matched = store.Invocations.Any(inv => InvocationContains(inv, jobId, serviceType));
        matched.Should().BeTrue(
            $"expected IWorkerResultStore to be called with jobId='{jobId}' and serviceType='{serviceType}'. " +
            $"Actual calls: {string.Join(", ", store.Invocations.Select(i => i.Method.Name))}");
    }

    private static bool InvocationContains(IInvocation inv, string jobId, ServiceType serviceType)
    {
        var hasJobId = inv.Arguments.Any(a => ArgMatchesJobId(a, jobId));
        var hasService = inv.Arguments.Any(a => ArgMatchesServiceType(a, serviceType));
        if (hasJobId && hasService) return true;

        // Sometimes embedded inside a single argument (e.g., WorkerResultData)
        foreach (var arg in inv.Arguments)
        {
            if (arg is null) continue;

            var embeddedJobId = TryGetString(arg, "JobId", "JobID", "Job");
            var embeddedService = TryGetServiceType(arg, "ServiceType", "Service", "Type");

            if (embeddedJobId == jobId && embeddedService == serviceType)
                return true;
        }

        return false;
    }

    private static bool ArgMatchesJobId(object? arg, string jobId)
    {
        if (arg is null) return false;
        if (arg is string s) return string.Equals(s, jobId, StringComparison.Ordinal);

        var embedded = TryGetString(arg, "JobId", "JobID", "Job");
        return embedded != null && string.Equals(embedded, jobId, StringComparison.Ordinal);
    }

    private static bool ArgMatchesServiceType(object? arg, ServiceType serviceType)
    {
        if (arg is null) return false;
        if (arg is ServiceType st) return st == serviceType;

        var embedded = TryGetServiceType(arg, "ServiceType", "Service", "Type");
        return embedded != null && embedded.Value == serviceType;
    }

    private static bool? TryExtractStoredSuccess(Mock<IWorkerResultStore> store, string jobId, ServiceType serviceType)
    {
        var inv = store.Invocations.FirstOrDefault(i => InvocationContains(i, jobId, serviceType));
        if (inv == null) return null;

        foreach (var arg in inv.Arguments)
        {
            if (arg is null) continue;

            if (arg is bool b) return b;

            var embedded = TryGetBool(arg, "Success", "IsSuccess");
            if (embedded != null) return embedded;
        }

        return null;
    }

    private static string? TryExtractStoredErrorMessage(Mock<IWorkerResultStore> store, string jobId, ServiceType serviceType)
    {
        var inv = store.Invocations.FirstOrDefault(i => InvocationContains(i, jobId, serviceType));
        if (inv == null) return null;

        foreach (var arg in inv.Arguments)
        {
            if (arg is null) continue;

            var err = TryGetString(arg, "ErrorMessage", "Error");
            if (!string.IsNullOrWhiteSpace(err))
                return err;

            // Sometimes error is passed as a raw string parameter
            if (arg is string s && s.Length > 0 && s.Contains("GeoIP", StringComparison.OrdinalIgnoreCase))
                return s;
        }

        return null;
    }

    private static string? TryExtractStoredJson(Mock<IWorkerResultStore> store, string jobId, ServiceType serviceType)
    {
        var inv = store.Invocations.FirstOrDefault(i => InvocationContains(i, jobId, serviceType));
        if (inv == null) return null;

        foreach (var arg in inv.Arguments)
        {
            if (arg is null) continue;

            // Direct string payload
            if (arg is string s && LooksLikeJsonObject(s))
                return s.Trim();

            // JsonDocument payload
            if (arg is JsonDocument jd)
                return jd.RootElement.ToString();

            // JsonElement payload
            if (arg is JsonElement je)
                return je.ToString();

            // Embedded Data property (e.g., WorkerResultData.Data)
            var dataProp = GetPropertyCaseInsensitive(arg, "Data");
            if (dataProp != null)
            {
                var dataVal = dataProp.GetValue(arg);

                if (dataVal is JsonDocument embeddedJd)
                    return embeddedJd.RootElement.ToString();

                if (dataVal is JsonElement embeddedJe)
                    return embeddedJe.ToString();

                if (dataVal is string embeddedS && LooksLikeJsonObject(embeddedS))
                    return embeddedS.Trim();
            }

            // If the result object itself is passed, serialize it (GeoIPResponse has Status/CountryCode/etc.)
            if (HasProperty(arg, "Status") && (HasProperty(arg, "CountryCode") || HasProperty(arg, "countryCode")))
            {
                try { return JsonSerializer.Serialize(arg); } catch { /* ignore */ }
            }
        }

        return null;
    }

    private static void AssertPublishedTaskCompleted(
        object published,
        string jobId,
        ServiceType serviceType,
        bool expectedSuccess,
        string? expectedErrorEquals = null,
        string? expectedErrorContains = null)
    {
        var publishedJobId = TryGetString(published, "JobId", "JobID");
        publishedJobId.Should().Be(jobId);

        var publishedService = TryGetServiceType(published, "ServiceType", "Service", "Type");
        publishedService.Should().Be(serviceType);

        var publishedSuccess = TryGetBool(published, "Success", "IsSuccess");
        publishedSuccess.Should().Be(expectedSuccess);

        var error = TryGetString(published, "ErrorMessage", "Error");
        if (expectedSuccess)
        {
            if (error is not null)
                error.Should().BeNullOrWhiteSpace();
        }
        else
        {
            error.Should().NotBeNullOrWhiteSpace();

            if (expectedErrorEquals != null)
                error.Should().Be(expectedErrorEquals);

            if (expectedErrorContains != null)
                error!.Should().Contain(expectedErrorContains);
        }

        var duration = TryGetTimeSpan(published, "Duration");
        if (duration != null)
            duration.Value.Should().BeGreaterOrEqualTo(TimeSpan.Zero);

        var durationMs = TryGetInt(published, "DurationMs", "DurationMS", "DurationMilliseconds");
        if (durationMs != null)
            durationMs.Value.Should().BeGreaterOrEqualTo(0);
    }

    // -----------------------
    // Helpers: JSON + reflection
    // -----------------------

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

    private static bool LooksLikeJsonObject(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s.StartsWith("{") && s.EndsWith("}");
    }

    private static PropertyInfo? GetPropertyCaseInsensitive(object obj, string name)
        => obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

    private static bool HasProperty(object obj, string propName)
        => GetPropertyCaseInsensitive(obj, propName) != null;

    private static string? TryGetString(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = GetPropertyCaseInsensitive(obj, name);
            if (prop == null) continue;

            var val = prop.GetValue(obj);
            if (val == null) continue;

            if (val is string s) return s;
            return val.ToString();
        }
        return null;
    }

    private static bool? TryGetBool(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = GetPropertyCaseInsensitive(obj, name);
            if (prop == null) continue;

            var val = prop.GetValue(obj);
            if (val == null) continue;

            if (val is bool b) return b;
            if (val is string s && bool.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    private static int? TryGetInt(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = GetPropertyCaseInsensitive(obj, name);
            if (prop == null) continue;

            var val = prop.GetValue(obj);
            if (val == null) continue;

            if (val is int i) return i;
            if (val is long l) return checked((int)l);
            if (val is string s && int.TryParse(s, out var parsed)) return parsed;
        }
        return null;
    }

    private static TimeSpan? TryGetTimeSpan(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = GetPropertyCaseInsensitive(obj, name);
            if (prop == null) continue;

            var val = prop.GetValue(obj);
            if (val == null) continue;

            if (val is TimeSpan ts) return ts;
        }
        return null;
    }

    private static ServiceType? TryGetServiceType(object obj, params string[] names)
    {
        foreach (var name in names)
        {
            var prop = GetPropertyCaseInsensitive(obj, name);
            if (prop == null) continue;

            var val = prop.GetValue(obj);
            if (val == null) continue;

            if (val is ServiceType st) return st;

            if (val is int i) return (ServiceType)i;
            if (val is long l) return (ServiceType)l;

            if (val is string s)
            {
                if (Enum.TryParse<ServiceType>(s, ignoreCase: true, out var parsed))
                    return parsed;

                if (int.TryParse(s, out var parsedInt))
                    return (ServiceType)parsedInt;
            }
        }

        return null;
    }

    // -----------------------
    // Moq: task-friendly defaults
    // -----------------------

    private static Mock<T> CreateTaskFriendlyMock<T>() where T : class
    {
        return new Mock<T>(MockBehavior.Loose)
        {
            DefaultValueProvider = new TaskFriendlyDefaultValueProvider()
        };
    }

    private sealed class TaskFriendlyDefaultValueProvider : DefaultValueProvider
    {
        protected override object GetDefaultValue(Type type, Mock mock)
        {
            if (type == typeof(Task))
                return Task.CompletedTask;

            if (type.IsGenericType && type.GetGenericTypeDefinition() == typeof(Task<>))
            {
                var resultType = type.GetGenericArguments()[0];
                var defaultResult = resultType.IsValueType ? Activator.CreateInstance(resultType) : null;

                var fromResult = typeof(Task)
                    .GetMethod(nameof(Task.FromResult))!
                    .MakeGenericMethod(resultType);

                return fromResult.Invoke(null, new[] { defaultResult })!;
            }

            return type.IsValueType ? Activator.CreateInstance(type)! : null!;
        }
    }

    // -----------------------
    // Http handler stub
    // -----------------------

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
