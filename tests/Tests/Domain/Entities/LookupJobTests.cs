using DistributedLookup.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace Tests.Domain.Entities;

public class LookupJobTests
{
    [Fact]
    public void Constructor_ShouldCreateJobWithPendingStatus_AndMetadata()
    {
        // Arrange
        var before = DateTime.UtcNow;

        var jobId = Guid.NewGuid().ToString();
        var target = "8.8.8.8";
        var services = new[] { ServiceType.GeoIP, ServiceType.Ping };

        // Act
        var job = new LookupJob(jobId, target, LookupTarget.IPAddress, services);

        var after = DateTime.UtcNow;

        // Assert
        job.JobId.Should().Be(jobId);
        job.Target.Should().Be(target);
        job.TargetType.Should().Be(LookupTarget.IPAddress);
        job.Status.Should().Be(JobStatus.Pending);

        job.CreatedAt.Should().BeOnOrAfter(before);
        job.CreatedAt.Should().BeOnOrBefore(after);
        job.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);

        job.CompletedAt.Should().BeNull();

        job.RequestedServices.Should().BeEquivalentTo(services);
        job.IsComplete().Should().BeFalse();
    }

    [Fact]
    public void Constructor_WithExplicitCreatedAt_ShouldUseProvidedTimestamp()
    {
        // Arrange
        var createdAt = DateTime.UtcNow.AddMinutes(-5);
        var jobId = Guid.NewGuid().ToString();
        var target = "example.com";
        var services = new[] { ServiceType.GeoIP };

        // Act
        var job = new LookupJob(jobId, target, LookupTarget.Domain, services, createdAt);

        // Assert
        job.CreatedAt.Should().Be(createdAt);
        job.CreatedAt.Kind.Should().Be(DateTimeKind.Utc);
        job.Status.Should().Be(JobStatus.Pending);
        job.CompletedAt.Should().BeNull();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WhenJobIdIsNullOrWhitespace_ShouldThrow(string? jobId)
    {
        // Arrange
        var target = "8.8.8.8";
        var services = new[] { ServiceType.GeoIP };

        // Act
        Action act = () => new LookupJob(jobId!, target, LookupTarget.IPAddress, services);

        // Assert
        var ex = act.Should().Throw<ArgumentException>()
            .WithMessage("JobId cannot be empty*")
            .Which;

        ex.ParamName.Should().Be("jobId");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Constructor_WhenTargetIsNullOrWhitespace_ShouldThrow(string? target)
    {
        // Arrange
        var jobId = Guid.NewGuid().ToString();
        var services = new[] { ServiceType.GeoIP };

        // Act
        Action act = () => new LookupJob(jobId, target!, LookupTarget.IPAddress, services);

        // Assert
        var ex = act.Should().Throw<ArgumentException>()
            .WithMessage("Target cannot be empty*")
            .Which;

        ex.ParamName.Should().Be("target");
    }

    [Fact]
    public void MarkAsProcessing_WhenPending_ShouldTransitionToProcessing()
    {
        // Arrange
        var job = CreateTestJob();

        // Act
        job.MarkAsProcessing();

        // Assert
        job.Status.Should().Be(JobStatus.Processing);
        job.CompletedAt.Should().BeNull();
        job.IsComplete().Should().BeFalse();
    }

    [Fact]
    public void MarkAsProcessing_WhenNotPending_ShouldThrowException()
    {
        // Arrange
        var job = CreateTestJob();
        job.MarkAsProcessing();

        // Act
        Action act = () => job.MarkAsProcessing();

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Cannot transition from*");
    }

    [Fact]
    public void MarkAsCompleted_ShouldSetStatusToCompleted_AndSetCompletedAtUtc()
    {
        // Arrange
        var job = CreateTestJob();
        var before = DateTime.UtcNow;

        // Act
        job.MarkAsCompleted();

        var after = DateTime.UtcNow;

        // Assert
        job.Status.Should().Be(JobStatus.Completed);
        job.CompletedAt.Should().NotBeNull();
        job.CompletedAt!.Value.Should().BeOnOrAfter(before);
        job.CompletedAt!.Value.Should().BeOnOrBefore(after);
        job.CompletedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

        job.IsComplete().Should().BeTrue();
    }

    [Fact]
    public void MarkAsFailed_ShouldSetStatusToFailed_AndSetCompletedAtUtc()
    {
        // Arrange
        var job = CreateTestJob();
        var before = DateTime.UtcNow;

        // Act
        job.MarkAsFailed("Test failure");

        var after = DateTime.UtcNow;

        // Assert
        job.Status.Should().Be(JobStatus.Failed);
        job.CompletedAt.Should().NotBeNull();
        job.CompletedAt!.Value.Should().BeOnOrAfter(before);
        job.CompletedAt!.Value.Should().BeOnOrBefore(after);
        job.CompletedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

        job.IsComplete().Should().BeTrue();
    }

    [Fact]
    public void IsComplete_ShouldReturnTrue_ForCompletedOrFailed()
    {
        // Completed
        var completedJob = CreateTestJob();
        completedJob.IsComplete().Should().BeFalse();
        completedJob.MarkAsCompleted();
        completedJob.IsComplete().Should().BeTrue();

        // Failed
        var failedJob = CreateTestJob();
        failedJob.IsComplete().Should().BeFalse();
        failedJob.MarkAsFailed("boom");
        failedJob.IsComplete().Should().BeTrue();
    }

    private static LookupJob CreateTestJob(ServiceType[]? services = null)
    {
        return new LookupJob(
            Guid.NewGuid().ToString(),
            "8.8.8.8",
            LookupTarget.IPAddress,
            services ?? new[] { ServiceType.GeoIP }
        );
    }
}
