using DistributedLookup.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace Tests.Domain.Entities;

public class LookupJobTests
{
    [Fact]
    public void Constructor_ShouldCreateJobWithPendingStatus()
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
        job.Results.Should().BeEmpty();
        job.IsComplete().Should().BeFalse();
        job.CompletionPercentage().Should().Be(0);
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
    public void CompletionPercentage_WhenNoServicesRequested_ShouldReturnZero()
    {
        // Arrange
        var job = CreateTestJob(Array.Empty<ServiceType>());

        // Assert
        job.RequestedServices.Should().BeEmpty();
        job.CompletionPercentage().Should().Be(0);
        job.IsComplete().Should().BeFalse();

        // And adding any result should fail because nothing was requested
        var result = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { }, TimeSpan.FromMilliseconds(100));
        Action act = () => job.AddResult(ServiceType.GeoIP, result);

        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not requested*");
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
            .WithMessage("*Processing*");
    }

    [Fact]
    public void AddResult_ShouldUpdateCompletionPercentage()
    {
        // Arrange
        var services = new[] { ServiceType.GeoIP, ServiceType.Ping };
        var job = CreateTestJob(services);

        var result = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { }, TimeSpan.FromMilliseconds(100));

        // Act
        job.AddResult(ServiceType.GeoIP, result);

        // Assert
        job.Results.Should().HaveCount(1);
        job.Results.Should().ContainKey(ServiceType.GeoIP);
        job.CompletionPercentage().Should().Be(50); // 1 out of 2
        job.Status.Should().Be(JobStatus.Pending); // AddResult doesn't change status unless completing
        job.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void AddResult_WhenSameServiceIsAddedAgain_BeforeCompletion_ShouldOverwriteAndNotChangePercentage()
    {
        // Arrange
        var services = new[] { ServiceType.GeoIP, ServiceType.Ping };
        var job = CreateTestJob(services);

        var result1 = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { v = 1 }, TimeSpan.FromMilliseconds(100));
        var result2 = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { v = 2 }, TimeSpan.FromMilliseconds(150));

        // Act
        job.AddResult(ServiceType.GeoIP, result1);
        job.AddResult(ServiceType.GeoIP, result2);

        // Assert
        job.Results.Should().HaveCount(1);
        job.CompletionPercentage().Should().Be(50);
        job.Status.Should().Be(JobStatus.Pending);
        job.CompletedAt.Should().BeNull();
    }

    [Fact]
    public void AddResult_WhenAllServicesComplete_ShouldMarkAsCompleted()
    {
        // Arrange
        var services = new[] { ServiceType.GeoIP, ServiceType.Ping };
        var job = CreateTestJob(services);

        var before = DateTime.UtcNow;

        var result1 = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { }, TimeSpan.FromMilliseconds(100));
        var result2 = ServiceResult.CreateSuccess(ServiceType.Ping, new { }, TimeSpan.FromMilliseconds(200));

        // Act
        job.AddResult(ServiceType.GeoIP, result1);
        job.AddResult(ServiceType.Ping, result2);

        var after = DateTime.UtcNow;

        // Assert
        job.Status.Should().Be(JobStatus.Completed);
        job.CompletedAt.Should().NotBeNull();
        job.CompletedAt!.Value.Should().BeOnOrAfter(before);
        job.CompletedAt!.Value.Should().BeOnOrBefore(after);
        job.CompletedAt!.Value.Kind.Should().Be(DateTimeKind.Utc);

        job.IsComplete().Should().BeTrue();
        job.CompletionPercentage().Should().Be(100);
    }

    [Fact]
    public void AddResult_ForUnrequestedService_ShouldThrowException()
    {
        // Arrange
        var job = CreateTestJob(new[] { ServiceType.GeoIP });
        var result = ServiceResult.CreateSuccess(ServiceType.Ping, new { }, TimeSpan.FromMilliseconds(100));

        // Act
        Action act = () => job.AddResult(ServiceType.Ping, result);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*not requested*");
    }

    [Fact]
    public void AddResult_WhenAlreadyCompleted_ShouldThrowException()
    {
        // Arrange
        var services = new[] { ServiceType.GeoIP };
        var job = CreateTestJob(services);

        var result = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { }, TimeSpan.FromMilliseconds(100));
        job.AddResult(ServiceType.GeoIP, result); // completes the job

        // Act - try to add another result
        var result2 = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { }, TimeSpan.FromMilliseconds(100));
        Action act = () => job.AddResult(ServiceType.GeoIP, result2);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Completed*");
    }

    [Fact]
    public void AddResult_WhenJobIsFailed_ShouldThrowException()
    {
        // Arrange
        var job = CreateTestJob(new[] { ServiceType.GeoIP });
        job.MarkAsFailed("Test failure");

        var result = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { }, TimeSpan.FromMilliseconds(100));

        // Act
        Action act = () => job.AddResult(ServiceType.GeoIP, result);

        // Assert
        act.Should().Throw<InvalidOperationException>()
            .WithMessage("*Failed*");
    }

    [Fact]
    public void MarkAsFailed_ShouldSetStatusToFailed()
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

    private LookupJob CreateTestJob(ServiceType[]? services = null)
    {
        return new LookupJob(
            Guid.NewGuid().ToString(),
            "8.8.8.8",
            LookupTarget.IPAddress,
            services ?? new[] { ServiceType.GeoIP }
        );
    }
}
