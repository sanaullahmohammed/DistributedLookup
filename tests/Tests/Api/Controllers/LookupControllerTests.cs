using System.Reflection;
using DistributedLookup.Api.Controllers;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Application.UseCases;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
using MassTransit;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Api.Controllers;

public class LookupControllerTests
{
    [Fact]
    public async Task Submit_WhenUseCaseValidationFails_ShouldReturnBadRequest_WithErrorResponse_AndNotCallRepoOrPublisher()
    {
        // Arrange (strict mocks: if repo/publisher get called, test fails)
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var logger = new Mock<ILogger<LookupController>>();

        var submitUseCase = new SubmitLookupJob(repo.Object, publisher.Object);

        // GetJobStatus isn't used in this test, but required by controller
        var statusRepo = new Mock<IJobRepository>(MockBehavior.Strict);
        var getStatusUseCase = new GetJobStatus(statusRepo.Object);

        var controller = CreateController(submitUseCase, getStatusUseCase, logger);

        // invalid target -> SubmitLookupJob returns failure ("Target must be a valid IP address or domain")
        var request = new LookupController.SubmitRequest { Target = "localhost" };

        // Act
        var result = await controller.Submit(request);

        // Assert
        result.Should().BeOfType<BadRequestObjectResult>();
        var badRequest = (BadRequestObjectResult)result;

        badRequest.StatusCode.Should().Be(StatusCodes.Status400BadRequest);
        badRequest.Value.Should().BeOfType<LookupController.ErrorResponse>();

        var payload = (LookupController.ErrorResponse)badRequest.Value!;
        payload.Error.Should().Be("Target must be a valid IP address or domain");

        // Ensure we logged receipt
        VerifyLoggerContains(logger, LogLevel.Information, "Received lookup request");

        // repo/publisher should not be called (strict mocks enforce this already)
        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task Submit_WhenSuccess_ShouldReturnAcceptedAtAction_WithSubmitResponse_AndStatusUrl_AndPublishEvent()
    {
        // Arrange
        var repo = new Mock<IJobRepository>(MockBehavior.Strict);
        var publisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var logger = new Mock<ILogger<LookupController>>();

        LookupJob? savedJob = null;
        JobSubmitted? publishedEvent = null;

        repo.Setup(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()))
            .Callback<LookupJob, CancellationToken>((job, _) => savedJob = job)
            .Returns(Task.CompletedTask);

        publisher.Setup(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()))
            .Callback<JobSubmitted, CancellationToken>((evt, _) => publishedEvent = evt)
            .Returns(Task.CompletedTask);

        var submitUseCase = new SubmitLookupJob(repo.Object, publisher.Object);

        // GetJobStatus isn't used in this test, but required by controller
        var statusRepo = new Mock<IJobRepository>(MockBehavior.Strict);
        var getStatusUseCase = new GetJobStatus(statusRepo.Object);

        var urlHelper = new Mock<IUrlHelper>(MockBehavior.Strict);
        urlHelper.Setup(u => u.Action(It.IsAny<UrlActionContext>()))
            .Returns((UrlActionContext ctx) =>
            {
                var jobId = GetAnonymousValue(ctx.Values, "jobId");
                return $"/api/lookup/{jobId}";
            });

        var controller = CreateController(submitUseCase, getStatusUseCase, logger, urlHelper.Object);

        var services = new[] { ServiceType.GeoIP, ServiceType.Ping };
        var request = new LookupController.SubmitRequest
        {
            Target = "8.8.8.8",
            Services = services
        };

        // Act
        var result = await controller.Submit(request);

        // Assert - action result type
        result.Should().BeOfType<AcceptedAtActionResult>();
        var accepted = (AcceptedAtActionResult)result;

        accepted.StatusCode.Should().Be(StatusCodes.Status202Accepted);
        accepted.ActionName.Should().Be(nameof(LookupController.GetStatus));
        accepted.RouteValues.Should().NotBeNull();
        accepted.RouteValues!.Should().ContainKey("jobId");

        // Assert - response payload
        accepted.Value.Should().BeOfType<LookupController.SubmitResponse>();
        var response = (LookupController.SubmitResponse)accepted.Value!;

        response.JobId.Should().NotBeNullOrWhiteSpace();
        Guid.TryParse(response.JobId, out _).Should().BeTrue("JobId should be a guid string");
        response.StatusUrl.Should().Be($"/api/lookup/{response.JobId}");
        response.Message.Should().Be("Job submitted successfully. Poll the status URL to check progress.");

        accepted?.RouteValues?["jobId"]!.ToString().Should().Be(response.JobId);

        // Assert - Url.Action called with correct action + route values
        urlHelper.Verify(u => u.Action(It.Is<UrlActionContext>(ctx =>
            string.Equals(ctx.Action, nameof(LookupController.GetStatus), StringComparison.Ordinal) &&
            GetAnonymousValue(ctx.Values, "jobId") == response.JobId
        )), Times.Once);

        // Assert - job persisted
        savedJob.Should().NotBeNull();
        savedJob!.JobId.Should().Be(response.JobId);
        savedJob.Target.Should().Be("8.8.8.8");
        savedJob.TargetType.Should().Be(LookupTarget.IPAddress);
        savedJob.RequestedServices.Should().BeEquivalentTo(services);

        // Assert - event published
        publishedEvent.Should().NotBeNull();
        publishedEvent!.JobId.Should().Be(response.JobId);
        publishedEvent.Target.Should().Be("8.8.8.8");
        publishedEvent.TargetType.Should().Be(LookupTarget.IPAddress);
        publishedEvent.Services.Should().BeEquivalentTo(services);
        publishedEvent.Timestamp.Should().BeCloseTo(DateTime.UtcNow, TimeSpan.FromSeconds(2));

        // Assert - log called
        VerifyLoggerContains(logger, LogLevel.Information, "Received lookup request");

        repo.Verify(r => r.SaveAsync(It.IsAny<LookupJob>(), It.IsAny<CancellationToken>()), Times.Once);
        publisher.Verify(p => p.Publish(It.IsAny<JobSubmitted>(), It.IsAny<CancellationToken>()), Times.Once);
        repo.VerifyNoOtherCalls();
        publisher.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetStatus_WhenJobNotFound_ShouldReturnNotFound_WithErrorResponse()
    {
        // Arrange
        var submitRepo = new Mock<IJobRepository>(MockBehavior.Strict);
        var submitPublisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var submitUseCase = new SubmitLookupJob(submitRepo.Object, submitPublisher.Object); // unused in this test

        var statusRepo = new Mock<IJobRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<LookupController>>();

        var jobId = Guid.NewGuid().ToString();

        statusRepo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((LookupJob?)null);

        var getStatusUseCase = new GetJobStatus(statusRepo.Object);

        var controller = CreateController(submitUseCase, getStatusUseCase, logger);

        // Act
        var result = await controller.GetStatus(jobId);

        // Assert
        result.Should().BeOfType<NotFoundObjectResult>();
        var notFound = (NotFoundObjectResult)result;

        notFound.StatusCode.Should().Be(StatusCodes.Status404NotFound);
        notFound.Value.Should().BeOfType<LookupController.ErrorResponse>();

        ((LookupController.ErrorResponse)notFound.Value!).Error.Should().Be("Job not found");

        VerifyLoggerContains(logger, LogLevel.Information, "Checking status");

        statusRepo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        statusRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public async Task GetStatus_WhenJobExists_ShouldReturnOk_WithGetJobStatusResponse()
    {
        // Arrange
        var submitRepo = new Mock<IJobRepository>(MockBehavior.Strict);
        var submitPublisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var submitUseCase = new SubmitLookupJob(submitRepo.Object, submitPublisher.Object); // unused

        var statusRepo = new Mock<IJobRepository>(MockBehavior.Strict);
        var logger = new Mock<ILogger<LookupController>>();

        var jobId = Guid.NewGuid().ToString();

        var job = new LookupJob(
            jobId,
            "example.com",
            LookupTarget.Domain,
            new[] { ServiceType.GeoIP, ServiceType.Ping });

        var geo = ServiceResult.CreateSuccess(ServiceType.GeoIP, new { country = "US" }, TimeSpan.FromMilliseconds(10));
        job.AddResult(ServiceType.GeoIP, geo);

        statusRepo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var getStatusUseCase = new GetJobStatus(statusRepo.Object);
        var controller = CreateController(submitUseCase, getStatusUseCase, logger);

        // Act
        var result = await controller.GetStatus(jobId);

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;

        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeOfType<GetJobStatus.Response>();

        var payload = (GetJobStatus.Response)ok.Value!;
        payload.JobId.Should().Be(jobId);
        payload.Target.Should().Be("example.com");
        payload.TargetType.Should().Be(LookupTarget.Domain);
        payload.Status.Should().Be(JobStatus.Pending);
        payload.CompletionPercentage.Should().Be(50);

        payload.Results.Should().ContainSingle(r => r.ServiceType == ServiceType.GeoIP && r.Success);

        VerifyLoggerContains(logger, LogLevel.Information, "Checking status");

        statusRepo.Verify(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()), Times.Once);
        statusRepo.VerifyNoOtherCalls();
    }

    [Fact]
    public void GetAvailableServices_ShouldReturnOk_WithServiceInfoForAllEnumValues()
    {
        // Arrange
        var submitRepo = new Mock<IJobRepository>(MockBehavior.Strict);
        var submitPublisher = new Mock<IPublishEndpoint>(MockBehavior.Strict);
        var submitUseCase = new SubmitLookupJob(submitRepo.Object, submitPublisher.Object);

        var statusRepo = new Mock<IJobRepository>(MockBehavior.Strict);
        var getStatusUseCase = new GetJobStatus(statusRepo.Object);

        var logger = new Mock<ILogger<LookupController>>();
        var controller = CreateController(submitUseCase, getStatusUseCase, logger);

        // Act
        var result = controller.GetAvailableServices();

        // Assert
        result.Should().BeOfType<OkObjectResult>();
        var ok = (OkObjectResult)result;

        ok.StatusCode.Should().Be(StatusCodes.Status200OK);
        ok.Value.Should().BeAssignableTo<LookupController.ServiceInfo[]>();

        var infos = (LookupController.ServiceInfo[])ok.Value!;
        infos.Length.Should().Be(Enum.GetValues<ServiceType>().Length);

        foreach (var info in infos)
        {
            info.Name.Should().Be(info.Value.ToString());

            var expectedDescription = info.Value switch
            {
                ServiceType.GeoIP => "Geographic location and ISP information",
                ServiceType.Ping => "Network reachability and latency check",
                ServiceType.RDAP => "Registration data via RDAP protocol",
                ServiceType.ReverseDNS => "Reverse DNS lookup (PTR record)",
                _ => "Unknown service"
            };

            info.Description.Should().Be(expectedDescription);
        }
    }

    private static LookupController CreateController(
        SubmitLookupJob submit,
        GetJobStatus status,
        Mock<ILogger<LookupController>> logger,
        IUrlHelper? urlHelper = null)
    {
        var controller = new LookupController(submit, status, logger.Object)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        if (urlHelper != null)
            controller.Url = urlHelper;

        return controller;
    }

    private static string? GetAnonymousValue(object? values, string name)
    {
        if (values == null) return null;

        var prop = values.GetType().GetProperty(
            name,
            BindingFlags.Public | BindingFlags.Instance | BindingFlags.IgnoreCase);

        return prop?.GetValue(values)?.ToString();
    }

    private static void VerifyLoggerContains(
        Mock<ILogger<LookupController>> logger,
        LogLevel level,
        string text)
    {
        logger.Verify(
            x => x.Log(
                It.Is<LogLevel>(l => l == level),
                It.IsAny<EventId>(),
                It.Is<It.IsAnyType>((v, _) =>
                    v.ToString()!.Contains(text, StringComparison.OrdinalIgnoreCase)),
                It.IsAny<Exception>(),
                It.IsAny<Func<It.IsAnyType, Exception?, string>>()),
            Times.AtLeastOnce);
    }
}
