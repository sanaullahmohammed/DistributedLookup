using DistributedLookup.Application.Interfaces;
using DistributedLookup.Application.UseCases;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
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

        var sut = new GetJobStatus(repo.Object);

        // Act
        var result = await sut.ExecuteAsync("missing", CancellationToken.None);

        // Assert
        result.Should().BeNull();

        repo.Verify(r => r.GetByIdAsync("missing", It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldPassCancellationTokenToRepository()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var sut = new GetJobStatus(repo.Object);

        var job = CreateJobWithNoResults();

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        repo.Setup(r => r.GetByIdAsync(job.JobId, token))
            .ReturnsAsync(job);

        // Act
        _ = await sut.ExecuteAsync(job.JobId, token);

        // Assert
        repo.Verify(r => r.GetByIdAsync(job.JobId, token), Times.Once);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenJobExists_ShouldMapTopLevelFields()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);

        var jobId = Guid.NewGuid().ToString();
        var job = new LookupJob(
            jobId,
            "8.8.8.8",
            LookupTarget.IPAddress,
            new[] { ServiceType.GeoIP, ServiceType.Ping });

        job.MarkAsProcessing();

        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var sut = new GetJobStatus(repo.Object);

        // Act
        var response = await sut.ExecuteAsync(jobId);

        // Assert
        response.Should().NotBeNull();
        response!.JobId.Should().Be(job.JobId);
        response.Target.Should().Be(job.Target);
        response.TargetType.Should().Be(job.TargetType);
        response.Status.Should().Be(job.Status);
        response.CreatedAt.Should().Be(job.CreatedAt);
        response.CompletedAt.Should().Be(job.CompletedAt); // should be null here

        response.CompletionPercentage.Should().Be(job.CompletionPercentage());
        response.RequestedServices.Should().BeEquivalentTo(job.RequestedServices);

        response.Results.Should().BeEmpty();

        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldMapResults_ForSuccessAndFailure()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);

        var jobId = Guid.NewGuid().ToString();
        var job = new LookupJob(
            jobId,
            "example.com",
            LookupTarget.Domain,
            new[] { ServiceType.GeoIP, ServiceType.Ping });

        var geo = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { country = "US" }, TimeSpan.FromMilliseconds(123));
        var ping = ServiceResult.CreateFailure(ServiceType.Ping, "timeout", TimeSpan.FromMilliseconds(456));

        // Add results => will complete job once both are added
        job.AddResult(ServiceType.GeoIP, geo);
        job.AddResult(ServiceType.Ping, ping);

        job.Status.Should().Be(JobStatus.Completed);
        job.CompletedAt.Should().NotBeNull();

        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var sut = new GetJobStatus(repo.Object);

        // Act
        var response = await sut.ExecuteAsync(jobId);

        // Assert
        response.Should().NotBeNull();
        response!.Status.Should().Be(JobStatus.Completed);
        response.CompletedAt.Should().NotBeNull();
        response.CompletionPercentage.Should().Be(100);

        response.Results.Should().HaveCount(2);

        var geoDto = response.Results.Single(r => r.ServiceType == ServiceType.GeoIP);
        geoDto.Success.Should().BeTrue();
        geoDto.ErrorMessage.Should().BeNull();
        geoDto.Data.Should().Be(geo.Data!.RootElement.ToString());
        geoDto.DurationMs.Should().Be(123);
        geoDto.CompletedAt.Should().BeCloseTo(geo.CompletedAt, TimeSpan.FromSeconds(1));

        var pingDto = response.Results.Single(r => r.ServiceType == ServiceType.Ping);
        pingDto.Success.Should().BeFalse();
        pingDto.ErrorMessage.Should().Be("timeout");
        pingDto.Data.Should().BeNull(); // CreateFailure doesn't set Data
        pingDto.DurationMs.Should().Be(456);
        pingDto.CompletedAt.Should().BeCloseTo(ping.CompletedAt, TimeSpan.FromSeconds(1));

        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldComputeCompletionPercentage_FromJob()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);

        var jobId = Guid.NewGuid().ToString();
        var job = new LookupJob(
            jobId,
            "8.8.8.8",
            LookupTarget.IPAddress,
            new[] { ServiceType.GeoIP, ServiceType.Ping });

        job.CompletionPercentage().Should().Be(0);

        job.AddResult(ServiceType.GeoIP,
            ServiceResult.CreateSuccess(ServiceType.GeoIP, new { }, TimeSpan.FromMilliseconds(10)));

        job.CompletionPercentage().Should().Be(50);

        repo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var sut = new GetJobStatus(repo.Object);

        // Act
        var response = await sut.ExecuteAsync(jobId);

        // Assert
        response.Should().NotBeNull();
        response!.CompletionPercentage.Should().Be(50);

        repo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();
    }

    private static LookupJob CreateJobWithNoResults()
    {
        var jobId = Guid.NewGuid().ToString();
        return new LookupJob(
            jobId,
            "8.8.8.8",
            LookupTarget.IPAddress,
            new[] { ServiceType.GeoIP });
    }
}
