using System;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Application.Workers;
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
    public async Task Consume_WhenPingThrows_ShouldStoreFailure_AndPublishTaskCompletedFailure()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();

        var msg = new CheckPing
        {
            JobId = jobId,
            Target = null!, // deterministic ArgumentNullException from SendPingAsync
            TargetType = LookupTarget.IPAddress
        };

        var resultStore = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<PingConsumer>>(MockBehavior.Loose);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(
            msg,
            onPublish: obj =>
            {
                published = obj;
                publishCount++;
            });

        var sut = new PingConsumer(logger.Object, resultStore.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - result store called for this job/service
        AssertResultStoreCalled(resultStore, jobId, ServiceType.Ping);

        // Assert - publish happened exactly once (either message overload or values overload)
        publishCount.Should().Be(1);
        published.Should().NotBeNull();

        AssertPublishedTaskCompleted(published!, jobId, ServiceType.Ping, expectedSuccess: false);

        // (Optional) if the result store call carried an error message, ensure it's non-empty
        var storedError = TryExtractStoredErrorMessage(resultStore, jobId, ServiceType.Ping);
        if (storedError is not null)
            storedError.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task Consume_WhenPingApiIsAvailable_ShouldStoreSuccess_AndPublishTaskCompletedSuccess()
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

        var resultStore = CreateTaskFriendlyMock<IWorkerResultStore>();
        var logger = new Mock<ILogger<PingConsumer>>(MockBehavior.Loose);

        object? published = null;
        var publishCount = 0;

        var ctx = CreateConsumeContext(
            msg,
            onPublish: obj =>
            {
                published = obj;
                publishCount++;
            });

        var sut = new PingConsumer(logger.Object, resultStore.Object);

        // Act
        await sut.Consume(ctx.Object);

        // Assert - result store called for this job/service
        AssertResultStoreCalled(resultStore, jobId, ServiceType.Ping);

        // Assert - publish happened exactly once
        publishCount.Should().Be(1);
        published.Should().NotBeNull();

        AssertPublishedTaskCompleted(published!, jobId, ServiceType.Ping, expectedSuccess: true);

        // Optional: if we can extract stored JSON from the store invocation, validate shape
        var storedJson = TryExtractStoredJson(resultStore, jobId, ServiceType.Ping);
        if (storedJson is not null)
        {
            using var doc = JsonDocument.Parse(storedJson);

            GetJsonInt32CaseInsensitive(doc.RootElement, "PacketsSent").Should().Be(4);
            GetJsonStringCaseInsensitive(doc.RootElement, "Target").Should().Be(target);

            var resultsEl = GetJsonElementCaseInsensitive(doc.RootElement, "Results");
            resultsEl.ValueKind.Should().Be(JsonValueKind.Array);
            resultsEl.GetArrayLength().Should().Be(4);
        }
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

    private static Mock<ConsumeContext<CheckPing>> CreateConsumeContext(CheckPing msg, Action<object> onPublish)
    {
        var ctx = new Mock<ConsumeContext<CheckPing>>(MockBehavior.Loose)
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

        // Sometimes both are embedded inside a single argument (e.g., WorkerResultData)
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

    private static void AssertPublishedTaskCompleted(object published, string jobId, ServiceType serviceType, bool expectedSuccess)
    {
        // TaskCompleted no longer has Data; assert only what we can safely rely on (via reflection).
        // Works whether publish is done with an actual TaskCompleted instance OR anonymous values object.

        var publishedJobId = TryGetString(published, "JobId", "JobID");
        publishedJobId.Should().Be(jobId);

        var publishedService = TryGetServiceType(published, "ServiceType", "Service", "Type");
        publishedService.Should().Be(serviceType);

        var publishedSuccess = TryGetBool(published, "Success", "IsSuccess");
        publishedSuccess.Should().Be(expectedSuccess);

        var error = TryGetString(published, "ErrorMessage", "Error");
        if (expectedSuccess)
        {
            // some implementations may omit the property or set it null/empty on success
            if (error is not null)
                error.Should().BeNullOrWhiteSpace();
        }
        else
        {
            error.Should().NotBeNullOrWhiteSpace();
        }

        // duration is nice-to-have; assert non-negative if present
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

            // Look for something like WorkerResultData.ErrorMessage or a direct string parameter
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
                return s;

            // JsonDocument payload
            if (arg is JsonDocument jd)
                return jd.RootElement.ToString();

            // Embedded Data property (e.g., WorkerResultData.Data)
            var dataProp = GetPropertyCaseInsensitive(arg, "Data");
            if (dataProp != null)
            {
                var dataVal = dataProp.GetValue(arg);

                if (dataVal is JsonDocument embeddedJd)
                    return embeddedJd.RootElement.ToString();

                if (dataVal is string embeddedS && LooksLikeJsonObject(embeddedS))
                    return embeddedS;
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

    private static Mock<T> CreateTaskFriendlyMock<T>() where T : class
    {
        return new Mock<T>(MockBehavior.Loose)
        {
            DefaultValueProvider = new TaskFriendlyDefaultValueProvider()
        };
    }

    /// <summary>
    /// Moq returns null for unexpected invocations on async members by default (Task / Task{T}),
    /// which can cause "await null" runtime failures.
    /// This provider returns completed Tasks with sensible defaults.
    /// </summary>
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
