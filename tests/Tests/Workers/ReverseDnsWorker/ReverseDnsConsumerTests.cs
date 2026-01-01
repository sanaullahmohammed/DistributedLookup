using System;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Application.Workers;
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
    public async Task Consume_WhenTargetTypeIsNotIp_ShouldStoreFailure_AndPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = "example.com",
            TargetType = LookupTarget.Domain
        };

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(
            msg,
            ct: CancellationToken.None,
            onPublish: p =>
            {
                published = p;
                publishCount++;
            });

        var sut = new ReverseDnsConsumer(logger.Object, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - result stored as failure
        AssertResultStoreCalled(store, jobId, ServiceType.ReverseDNS);

        var storedError = TryExtractStoredErrorMessage(store, jobId, ServiceType.ReverseDNS);
        if (storedError is not null)
            storedError.Should().Be("Reverse DNS lookup requires an IP address target.");

        // Assert - saga notified (publish)
        publishCount.Should().Be(1);
        published.Should().NotBeNull();
        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.ReverseDNS,
            expectedSuccess: false,
            expectedErrorEquals: "Reverse DNS lookup requires an IP address target.");
    }

    [Fact]
    public async Task Consume_WhenTargetIsNotValidIp_ShouldStoreFailure_AndPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = "not-an-ip",
            TargetType = LookupTarget.IPAddress
        };

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(
            msg,
            ct: CancellationToken.None,
            onPublish: p =>
            {
                published = p;
                publishCount++;
            });

        var sut = new ReverseDnsConsumer(logger.Object, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - result stored as failure
        AssertResultStoreCalled(store, jobId, ServiceType.ReverseDNS);

        var storedError = TryExtractStoredErrorMessage(store, jobId, ServiceType.ReverseDNS);
        if (storedError is not null)
            storedError.Should().Be("Invalid IP address: not-an-ip");

        // Assert - published failure
        publishCount.Should().Be(1);
        published.Should().NotBeNull();
        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.ReverseDNS,
            expectedSuccess: false,
            expectedErrorEquals: "Invalid IP address: not-an-ip");
    }

    [Fact]
    public async Task Consume_WhenDnsLookupTimesOut_ShouldStoreFailure_AndPublishFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        // Pick a "random-ish" TEST-NET IP to reduce the chance of synchronous completion/caching.
        // We rely on the canceled token causing the timeout path.
        var msg = new CheckReverseDNS
        {
            JobId = jobId,
            Target = "203.0.113.123",
            TargetType = LookupTarget.IPAddress
        };

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        using var cts = new CancellationTokenSource();
        cts.Cancel(); // makes Task.Delay(...) complete immediately

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(
            msg,
            ct: cts.Token,
            onPublish: p =>
            {
                published = p;
                publishCount++;
            });

        var sut = new ReverseDnsConsumer(logger.Object, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert
        AssertResultStoreCalled(store, jobId, ServiceType.ReverseDNS);

        publishCount.Should().Be(1);
        published.Should().NotBeNull();

        // Message comes from: throw new TimeoutException($"Reverse DNS lookup timed out after 5s.")
        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.ReverseDNS,
            expectedSuccess: false,
            expectedErrorContains: "Reverse DNS lookup timed out after 5s");
    }

    [Fact]
    public async Task Consume_WhenPtrRecordExists_ShouldStoreSuccess_AndPublishSuccess()
    {
        // Environment-dependent. Probe first and skip if reverse DNS is not usable here.
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

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(
            msg,
            ct: CancellationToken.None,
            onPublish: p =>
            {
                published = p;
                publishCount++;
            });

        var sut = new ReverseDnsConsumer(logger.Object, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - stored success
        AssertResultStoreCalled(store, jobId, ServiceType.ReverseDNS);

        // Assert - published success
        publishCount.Should().Be(1);
        published.Should().NotBeNull();
        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.ReverseDNS,
            expectedSuccess: true);

        // Assert - stored JSON has Found=true, HostName not empty
        var json = TryExtractStoredJson(store, jobId, ServiceType.ReverseDNS);
        json.Should().NotBeNullOrWhiteSpace("successful lookups should store data");

        using var doc = JsonDocument.Parse(json!);
        GetJsonStringCaseInsensitive(doc.RootElement, "Input").Should().Be(target);
        doc.RootElement.GetProperty("Found").GetBoolean().Should().BeTrue();

        doc.RootElement.TryGetProperty("HostName", out var hostEl).Should().BeTrue();
        hostEl.ValueKind.Should().Be(JsonValueKind.String);
        hostEl.GetString().Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Consume_WhenNoPtrRecord_CommonSocketException_ShouldStoreSuccessFoundFalse_AndPublishSuccess()
    {
        // TEST-NET-1 IP; probe to ensure environment hits HostNotFound/NoData quickly.
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

        var store = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<ReverseDnsConsumer>>(MockBehavior.Loose);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(
            msg,
            ct: CancellationToken.None,
            onPublish: p =>
            {
                published = p;
                publishCount++;
            });

        var sut = new ReverseDnsConsumer(logger.Object, store.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - stored success (no PTR is a valid “success” result with Found=false)
        AssertResultStoreCalled(store, jobId, ServiceType.ReverseDNS);

        publishCount.Should().Be(1);
        published.Should().NotBeNull();
        AssertPublishedTaskCompleted(
            published!,
            jobId,
            ServiceType.ReverseDNS,
            expectedSuccess: true);

        var json = TryExtractStoredJson(store, jobId, ServiceType.ReverseDNS);
        json.Should().NotBeNullOrWhiteSpace("no-PTR outcomes should still store data");

        using var doc = JsonDocument.Parse(json!);
        GetJsonStringCaseInsensitive(doc.RootElement, "Input").Should().Be(target);
        doc.RootElement.GetProperty("Found").GetBoolean().Should().BeFalse();

        // HostName should be null in that branch
        doc.RootElement.TryGetProperty("HostName", out var hostEl).Should().BeTrue();
        hostEl.ValueKind.Should().Be(JsonValueKind.Null);
    }

    // -----------------------
    // ConsumeContext + publish capture
    // -----------------------

    private static Mock<ConsumeContext<CheckReverseDNS>> CreateConsumeContext(
        CheckReverseDNS msg,
        CancellationToken ct,
        Action<object> onPublish)
    {
        var ctx = new Mock<ConsumeContext<CheckReverseDNS>>(MockBehavior.Loose)
        {
            DefaultValueProvider = new TaskFriendlyDefaultValueProvider()
        };

        ctx.SetupGet(c => c.Message).Returns(msg);
        ctx.SetupGet(c => c.CancellationToken).Returns(ct);

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
    // Environment probes (skip helpers)
    // -----------------------

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

    // -----------------------
    // Assertions: store + publish
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

    private static string? TryExtractStoredErrorMessage(Mock<IWorkerResultStore> store, string jobId, ServiceType serviceType)
    {
        var inv = store.Invocations.FirstOrDefault(i => InvocationContains(i, jobId, serviceType));
        if (inv == null) return null;

        foreach (var arg in inv.Arguments)
        {
            if (arg is null) continue;

            // direct string parameter or embedded ErrorMessage property
            var err = TryGetString(arg, "ErrorMessage", "Error");
            if (!string.IsNullOrWhiteSpace(err))
                return err;
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

            // Fallback: if the argument itself looks like the lookup result object, serialize it
            if (HasProperty(arg, "Input") && HasProperty(arg, "Found"))
            {
                try
                {
                    return JsonSerializer.Serialize(arg);
                }
                catch
                {
                    // ignore and keep searching
                }
            }
        }

        return null;
    }

    private static bool LooksLikeJsonObject(string s)
    {
        if (string.IsNullOrWhiteSpace(s)) return false;
        s = s.Trim();
        return s.StartsWith("{") && s.EndsWith("}");
    }

    private static bool HasProperty(object obj, string propName)
        => GetPropertyCaseInsensitive(obj, propName) != null;

    // -----------------------
    // Reflection helpers
    // -----------------------

    private static PropertyInfo? GetPropertyCaseInsensitive(object obj, string name)
        => obj.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

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
    // JSON helpers
    // -----------------------

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
}
