using System;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Application.Saga;
using DistributedLookup.Application.UseCases;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Application.UseCases;

public class GetJobStatusTests
{
    [Fact]
    public async Task ExecuteAsync_WhenJobDoesNotExist_ShouldReturnNull()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJob?)null);

        var sagaRepo = new Mock<ISagaStateRepository>(MockBehavior.Strict);
        var reader = new Mock<IWorkerResultReader>(MockBehavior.Strict);
        var logger = new Mock<ILogger<GetJobStatus>>(MockBehavior.Loose);

        var sut = new GetJobStatus(repo.Object, sagaRepo.Object, reader.Object, logger.Object);

        // Act
        var result = await sut.ExecuteAsync("missing", CancellationToken.None);

        // Assert
        result.Should().BeNull();

        repo.Verify(r => r.GetByIdAsync("missing", It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();
        sagaRepo.VerifyNoOtherCalls();
        reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassCancellationTokenToAllDependencies()
    {
        // Arrange
        var job = CreateJob(jobId: Guid.NewGuid().ToString(), services: new[] { ServiceType.GeoIP });

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(job.JobId, token))
            .ReturnsAsync(job);

        var sagaRepo = new Mock<ISagaStateRepository>(MockBehavior.Strict);
        sagaRepo.Setup(s => s.GetByJobIdAsync(job.JobId, token))
            .ReturnsAsync((LookupJobState)null!); // ✅ Task<LookupJobState>

        var reader = new Mock<IWorkerResultReader>(MockBehavior.Strict);
        reader.Setup(r => r.GetResultAsync(job.JobId, ServiceType.GeoIP, token))
            .ReturnsAsync((WorkerResultData)null!);

        var logger = new Mock<ILogger<GetJobStatus>>(MockBehavior.Loose);

        var sut = new GetJobStatus(repo.Object, sagaRepo.Object, reader.Object, logger.Object);

        // Act
        _ = await sut.ExecuteAsync(job.JobId, token);

        // Assert
        repo.Verify(r => r.GetByIdAsync(job.JobId, token), Times.Once);
        sagaRepo.Verify(s => s.GetByJobIdAsync(job.JobId, token), Times.Once);
        reader.Verify(r => r.GetResultAsync(job.JobId, ServiceType.GeoIP, token), Times.Once);

        repo.VerifyNoOtherCalls();
        sagaRepo.VerifyNoOtherCalls();
        reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenNoSagaStateAndNoWorkerResults_ShouldReturnPending_AndNoResults()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var job = CreateJob(jobId, "8.8.8.8", LookupTarget.IPAddress, new[] { ServiceType.GeoIP, ServiceType.Ping });

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var sagaRepo = new Mock<ISagaStateRepository>(MockBehavior.Strict);
        sagaRepo.Setup(s => s.GetByJobIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJobState)null!);

        var reader = new Mock<IWorkerResultReader>(MockBehavior.Strict);
        reader.Setup(r => r.GetResultAsync(jobId, ServiceType.GeoIP, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkerResultData)null!);
        reader.Setup(r => r.GetResultAsync(jobId, ServiceType.Ping, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkerResultData)null!);

        var logger = new Mock<ILogger<GetJobStatus>>(MockBehavior.Loose);

        var sut = new GetJobStatus(repo.Object, sagaRepo.Object, reader.Object, logger.Object);

        // Act
        var response = await sut.ExecuteAsync(jobId);

        // Assert
        response.Should().NotBeNull();
        response!.JobId.Should().Be(job.JobId);
        response.Target.Should().Be(job.Target);
        response.TargetType.Should().Be(job.TargetType);

        response.Status.Should().Be(JobStatus.Pending);
        response.CompletedAt.Should().BeNull();
        response.CompletionPercentage.Should().Be(0);

        response.RequestedServices.Should().BeEquivalentTo(job.RequestedServices);
        response.Results.Should().BeEmpty();
        response.Warnings.Should().BeNull();

        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        sagaRepo.Verify(s => s.GetByJobIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        reader.Verify(r => r.GetResultAsync(jobId, ServiceType.GeoIP, It.IsAny<CancellationToken>()), Times.Once);
        reader.Verify(r => r.GetResultAsync(jobId, ServiceType.Ping, It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
        sagaRepo.VerifyNoOtherCalls();
        reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMapResults_ForSuccessAndFailure_AndMarkCompleted_WhenAllResultsExist()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var job = CreateJob(jobId, "example.com", LookupTarget.Domain, new[] { ServiceType.GeoIP, ServiceType.Ping });

        using var geoJson = JsonDocument.Parse(@"{""country"":""US""}");

        var geoCompleted = DateTime.UtcNow.AddSeconds(-2);
        var pingCompleted = DateTime.UtcNow.AddSeconds(-1);

        var geo = CreateWorkerResultData(
            success: true,
            data: geoJson,
            errorMessage: null,
            completedAt: geoCompleted,
            duration: TimeSpan.FromMilliseconds(123));

        var ping = CreateWorkerResultData(
            success: false,
            data: null,
            errorMessage: "timeout",
            completedAt: pingCompleted,
            duration: TimeSpan.FromMilliseconds(456));

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var sagaRepo = new Mock<ISagaStateRepository>(MockBehavior.Strict);
        sagaRepo.Setup(s => s.GetByJobIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJobState)null!);

        var reader = new Mock<IWorkerResultReader>(MockBehavior.Strict);
        reader.Setup(r => r.GetResultAsync(jobId, ServiceType.GeoIP, It.IsAny<CancellationToken>()))
            .ReturnsAsync(geo);
        reader.Setup(r => r.GetResultAsync(jobId, ServiceType.Ping, It.IsAny<CancellationToken>()))
            .ReturnsAsync(ping);

        var logger = new Mock<ILogger<GetJobStatus>>(MockBehavior.Loose);

        var sut = new GetJobStatus(repo.Object, sagaRepo.Object, reader.Object, logger.Object);

        // Act
        var response = await sut.ExecuteAsync(jobId);

        // Assert
        response.Should().NotBeNull();
        response!.Status.Should().Be(JobStatus.Completed);
        response.CompletionPercentage.Should().Be(100);

        // ✅ This will now match pingCompleted (not DateTime.MinValue)
        response.CompletedAt.Should().Be(pingCompleted);

        response.Results.Should().HaveCount(2);

        var geoDto = response.Results.Single(r => r.ServiceType == ServiceType.GeoIP);
        geoDto.Success.Should().BeTrue();
        geoDto.ErrorMessage.Should().BeNull();
        geoDto.Data.Should().Be(geoJson.RootElement.ToString());
        geoDto.DurationMs.Should().Be(123);
        geoDto.CompletedAt.Should().Be(geoCompleted);

        var pingDto = response.Results.Single(r => r.ServiceType == ServiceType.Ping);
        pingDto.Success.Should().BeFalse();
        pingDto.ErrorMessage.Should().Be("timeout");
        pingDto.Data.Should().BeNull();
        pingDto.DurationMs.Should().Be(456);
        pingDto.CompletedAt.Should().Be(pingCompleted);

        response.Warnings.Should().BeNull();

        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        sagaRepo.Verify(s => s.GetByJobIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        reader.Verify(r => r.GetResultAsync(jobId, ServiceType.GeoIP, It.IsAny<CancellationToken>()), Times.Once);
        reader.Verify(r => r.GetResultAsync(jobId, ServiceType.Ping, It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
        sagaRepo.VerifyNoOtherCalls();
        reader.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenOnlySomeResultsExist_ShouldReturnProcessing_AndPercentageFromResults()
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var job = CreateJob(jobId, "8.8.8.8", LookupTarget.IPAddress, new[] { ServiceType.GeoIP, ServiceType.Ping });

        using var geoJson = JsonDocument.Parse(@"{""x"":1}");

        var geo = CreateWorkerResultData(
            success: true,
            data: geoJson,
            errorMessage: null,
            completedAt: DateTime.UtcNow.AddSeconds(-1),
            duration: TimeSpan.FromMilliseconds(10));

        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var sagaRepo = new Mock<ISagaStateRepository>(MockBehavior.Strict);
        sagaRepo.Setup(s => s.GetByJobIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJobState)null!);

        var reader = new Mock<IWorkerResultReader>(MockBehavior.Strict);
        reader.Setup(r => r.GetResultAsync(jobId, ServiceType.GeoIP, It.IsAny<CancellationToken>()))
            .ReturnsAsync(geo);
        reader.Setup(r => r.GetResultAsync(jobId, ServiceType.Ping, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkerResultData)null!);

        var logger = new Mock<ILogger<GetJobStatus>>(MockBehavior.Loose);

        var sut = new GetJobStatus(repo.Object, sagaRepo.Object, reader.Object, logger.Object);

        // Act
        var response = await sut.ExecuteAsync(jobId);

        // Assert
        response.Should().NotBeNull();
        response!.Status.Should().Be(JobStatus.Processing);
        response.CompletionPercentage.Should().Be(50);
        response.CompletedAt.Should().BeNull();
        response.Results.Should().HaveCount(1);

        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        sagaRepo.Verify(s => s.GetByJobIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        reader.Verify(r => r.GetResultAsync(jobId, ServiceType.GeoIP, It.IsAny<CancellationToken>()), Times.Once);
        reader.Verify(r => r.GetResultAsync(jobId, ServiceType.Ping, It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
        sagaRepo.VerifyNoOtherCalls();
        reader.VerifyNoOtherCalls();
    }

    // -----------------------
    // Helpers
    // -----------------------

    private static LookupJob CreateJob(string jobId, string target, LookupTarget targetType, ServiceType[] services)
        => new(jobId, target, targetType, services);

    private static LookupJob CreateJob(string jobId, ServiceType[] services)
        => new(jobId, "8.8.8.8", LookupTarget.IPAddress, services);

    private static WorkerResultData CreateWorkerResultData(
        bool success,
        JsonDocument? data,
        string? errorMessage,
        DateTime completedAt,
        TimeSpan duration)
    {
        var t = typeof(WorkerResultData);

        // Prefer parameterless (public or non-public). If none, create uninitialized.
        object instance =
            Activator.CreateInstance(t, nonPublic: true)
            ?? RuntimeHelpers.GetUninitializedObject(t);

        // ALWAYS set members, regardless of how we created it
        SetMember(instance, "Success", success);
        SetMember(instance, "Data", data);
        SetMember(instance, "ErrorMessage", errorMessage);
        SetMember(instance, "CompletedAt", completedAt);
        SetMember(instance, "Duration", duration);

        return (WorkerResultData)instance;
    }

    private static void SetMember(object instance, string name, object? value)
    {
        var type = instance.GetType();

        // Try property (public/non-public), including init/private setters
        var prop = type.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (prop != null)
        {
            var setter = prop.GetSetMethod(nonPublic: true);
            if (setter != null)
            {
                setter.Invoke(instance, new[] { value });
                return;
            }

            // Some runtimes still allow SetValue for init-only
            if (prop.CanWrite)
            {
                prop.SetValue(instance, value);
                return;
            }
        }

        // Try auto-property backing field: <Name>k__BackingField
        var backingField = type.GetField($"<{name}>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic);
        if (backingField != null)
        {
            backingField.SetValue(instance, value);
            return;
        }

        // Try a field with the same name
        var field = type.GetField(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field != null)
        {
            field.SetValue(instance, value);
            return;
        }

        throw new InvalidOperationException($"Unable to set '{name}' on {type.FullName} for test data.");
    }
}
