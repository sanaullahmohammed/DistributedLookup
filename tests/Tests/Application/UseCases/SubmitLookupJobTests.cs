using DistributedLookup.Application.Interfaces;
using DistributedLookup.Application.UseCases;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
using MassTransit;
using Moq;
using Xunit;

namespace Tests.Application.UseCases;

public class SubmitLookupJobTests
{
    [Fact]
    public async Task ExecuteAsync_WhenTargetIsEmpty_ShouldReturnFailure_AndNotCallDependencies()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        // Act
        var result = await sut.ExecuteAsync(new SubmitLookupJob.Request(""));

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.JobId.Should().BeNull();
        result.Error.Should().Be("Target cannot be empty");

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetIsInvalid_ShouldReturnFailure_AndNotCallDependencies()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        // Act
        var result = await sut.ExecuteAsync(new SubmitLookupJob.Request("localhost")); // no dot, not IP

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.JobId.Should().BeNull();
        result.Error.Should().Be("Target must be a valid IP address or domain name");

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetIsValidIp_AndServicesNotProvided_ShouldUseDefaultServices_SaveJob_AndPublishJobSubmitted()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        var request = new SubmitLookupJob.Request("8.8.8.8");
        var expectedServices = new[]
        {
            ServiceType.GeoIP,
            ServiceType.Ping,
            ServiceType.RDAP,
            ServiceType.ReverseDNS
        };

        LookupJob? savedJob = null;
        CancellationToken savedToken = default;

        JobSubmitted? publishedEvent = null;
        CancellationToken publishedToken = default;

        var seq = new MockSequence();

        repo.InSequence(seq)
            .Setup(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()))
            .Callback<LookupJob, CancellationToken>((job, ct) =>
            {
                savedJob = job;
                savedToken = ct;
            })
            .Returns(Task.CompletedTask);

        publisher.InSequence(seq)
            .Setup(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()))
            .Callback<JobSubmitted, CancellationToken>((evt, ct) =>
            {
                publishedEvent = evt;
                publishedToken = ct;
            })
            .Returns(Task.CompletedTask);

        using var cts = new CancellationTokenSource();
        var token = cts.Token;

        var before = DateTime.UtcNow;

        // Act
        var result = await sut.ExecuteAsync(request, token);

        var after = DateTime.UtcNow;

        // Assert - result
        result.IsSuccess.Should().BeTrue();
        result.Error.Should().BeNull();
        result.JobId.Should().NotBeNull();
        Guid.TryParse(result.JobId, out _).Should().BeTrue("JobId should be a guid string");

        // Assert - SaveAsync
        savedToken.Should().Be(token);
        savedJob.Should().NotBeNull();
        savedJob!.JobId.Should().Be(result.JobId);
        savedJob.Target.Should().Be(request.Target);
        savedJob.TargetType.Should().Be(LookupTarget.IPAddress);
        savedJob.Status.Should().Be(JobStatus.Pending);
        savedJob.RequestedServices.Should().BeEquivalentTo(expectedServices);

        // Assert - Publish
        publishedToken.Should().Be(token);
        publishedEvent.Should().NotBeNull();
        publishedEvent!.JobId.Should().Be(result.JobId);
        publishedEvent.Target.Should().Be(request.Target);
        publishedEvent.TargetType.Should().Be(LookupTarget.IPAddress);
        publishedEvent.Services.Should().BeEquivalentTo(expectedServices);

        publishedEvent.Timestamp.Should().BeOnOrAfter(before);
        publishedEvent.Timestamp.Should().BeOnOrBefore(after);

        repo.Verify(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()), Times.Once);
        publisher.Verify(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetIsDomain_AndServicesProvided_ShouldUseProvidedServices_SaveJob_AndPublishJobSubmitted()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        var providedServices = new[] { ServiceType.RDAP, ServiceType.ReverseDNS };
        var request = new SubmitLookupJob.Request("example.com", providedServices);

        LookupJob? savedJob = null;
        JobSubmitted? publishedEvent = null;

        repo.Setup(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()))
            .Callback<LookupJob, CancellationToken>((job, _) => savedJob = job)
            .Returns(Task.CompletedTask);

        publisher.Setup(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()))
            .Callback<JobSubmitted, CancellationToken>((evt, _) => publishedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.ExecuteAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.JobId.Should().NotBeNull();

        savedJob.Should().NotBeNull();
        savedJob!.JobId.Should().Be(result.JobId);
        savedJob.Target.Should().Be("example.com");
        savedJob.TargetType.Should().Be(LookupTarget.Domain);
        savedJob.RequestedServices.Should().BeEquivalentTo(providedServices);

        publishedEvent.Should().NotBeNull();
        publishedEvent!.JobId.Should().Be(result.JobId);
        publishedEvent.Target.Should().Be("example.com");
        publishedEvent.TargetType.Should().Be(LookupTarget.Domain);
        publishedEvent.Services.Should().BeEquivalentTo(providedServices);

        repo.Verify(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()), Times.Once);
        publisher.Verify(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenServicesProvidedButEmpty_ShouldFallBackToDefaultServices()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        var request = new SubmitLookupJob.Request("8.8.8.8", Enumerable.Empty<ServiceType>());

        var expectedServices = new[]
        {
            ServiceType.GeoIP,
            ServiceType.Ping,
            ServiceType.RDAP,
            ServiceType.ReverseDNS
        };

        LookupJob? savedJob = null;
        JobSubmitted? publishedEvent = null;

        repo.Setup(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()))
            .Callback<LookupJob, CancellationToken>((job, _) => savedJob = job)
            .Returns(Task.CompletedTask);

        publisher.Setup(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()))
            .Callback<JobSubmitted, CancellationToken>((evt, _) => publishedEvent = evt)
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.ExecuteAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();

        savedJob.Should().NotBeNull();
        savedJob!.RequestedServices.Should().BeEquivalentTo(expectedServices);

        publishedEvent.Should().NotBeNull();
        publishedEvent!.Services.Should().BeEquivalentTo(expectedServices);

        repo.Verify(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()), Times.Once);
        publisher.Verify(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_ShouldSaveBeforePublishing()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        var request = new SubmitLookupJob.Request("example.com", new[] { ServiceType.RDAP });

        var seq = new MockSequence();

        repo.InSequence(seq)
            .Setup(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        publisher.InSequence(seq)
            .Setup(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Act
        var result = await sut.ExecuteAsync(request);

        // Assert
        result.IsSuccess.Should().BeTrue();

        repo.Verify(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()), Times.Once);
        publisher.Verify(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()), Times.Once);

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    #region Additional Validation Tests

    [Theory]
    [InlineData("1.1.1.1.1.1")]           // Too many octets
    [InlineData("286.4345.3244321.45345")] // Invalid octets
    [InlineData("256.1.1.1")]             // Octet > 255
    public async Task ExecuteAsync_WhenTargetIsInvalidIPv4_ShouldReturnFailure(string target)
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        // Act
        var result = await sut.ExecuteAsync(new SubmitLookupJob.Request(target));

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().Contain("Invalid IP address format");

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("::1")]                   // IPv6 loopback
    [InlineData("2001:db8::1")]           // IPv6 documentation
    [InlineData("fe80::1")]               // IPv6 link-local
    public async Task ExecuteAsync_WhenTargetIsValidIPv6_ShouldSucceed(string target)
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        repo.Setup(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        publisher.Setup(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        // Act
        var result = await sut.ExecuteAsync(new SubmitLookupJob.Request(target));

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.JobId.Should().NotBeNull();

        repo.Verify(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Theory]
    [InlineData("domain.123")]            // Numeric TLD
    [InlineData("test.456")]              // Numeric TLD
    public async Task ExecuteAsync_WhenTargetHasNumericTLD_ShouldReturnFailure(string target)
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        // Act
        var result = await sut.ExecuteAsync(new SubmitLookupJob.Request(target));

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Theory]
    [InlineData("www.google.com")]
    [InlineData("sub.domain.example.com")]
    [InlineData("a.io")]
    public async Task ExecuteAsync_WhenTargetIsValidDomain_ShouldSucceed(string target)
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        LookupJob? savedJob = null;

        repo.Setup(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()))
            .Callback<LookupJob, CancellationToken>((job, _) => savedJob = job)
            .Returns(Task.CompletedTask);

        publisher.Setup(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        // Act
        var result = await sut.ExecuteAsync(new SubmitLookupJob.Request(target));

        // Assert
        result.IsSuccess.Should().BeTrue();
        savedJob.Should().NotBeNull();
        savedJob!.TargetType.Should().Be(LookupTarget.Domain);

        repo.Verify(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetHasConsecutiveDots_ShouldReturnFailure()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        // Act
        var result = await sut.ExecuteAsync(new SubmitLookupJob.Request("domain..com"));

        // Assert
        result.IsSuccess.Should().BeFalse();
        result.Error.Should().NotBeNullOrEmpty();

        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task ExecuteAsync_WhenTargetHasLeadingTrailingWhitespace_ShouldValidateSuccessfully()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);

        repo.Setup(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        publisher.Setup(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var sut = new SubmitLookupJob(repo.Object, publisher.Object);

        // Act - whitespace around valid IP should still validate successfully
        var result = await sut.ExecuteAsync(new SubmitLookupJob.Request("  8.8.8.8  "));

        // Assert
        result.IsSuccess.Should().BeTrue();
        result.JobId.Should().NotBeNull();

        repo.Verify(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    #endregion
}
