using System;
using System.Collections.Generic;
using System.Linq;
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
using Microsoft.AspNetCore.Routing;
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
        var getStatusUseCase = CreateGetStatusUseCase(CreateTaskFriendlyMock<IJobRepository>().Object);

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
        var getStatusUseCase = CreateGetStatusUseCase(CreateTaskFriendlyMock<IJobRepository>().Object);

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

        var getStatusUseCase = CreateGetStatusUseCase(statusRepo.Object);

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

        // NOTE: LookupJob no longer has AddResult(); we only assert the controller returns Ok
        // and that the response is structurally valid.

        statusRepo.Setup(r => r.GetByIdAsync(jobId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(job);

        var getStatusUseCase = CreateGetStatusUseCase(statusRepo.Object);
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

        // Keep assertions resilient to internal GetJobStatus implementation changes
        payload.CompletionPercentage.Should().BeInRange(0, 100);
        Enum.IsDefined(typeof(JobStatus), payload.Status).Should().BeTrue();

        payload.Results.Should().NotBeNull();

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

        var getStatusUseCase = CreateGetStatusUseCase(CreateTaskFriendlyMock<IJobRepository>().Object);

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

        var expectedServices = Enum.GetValues<ServiceType>().ToHashSet();
        var returnedServices = new HashSet<ServiceType>();

        foreach (var info in infos)
        {
            Enum.TryParse<ServiceType>(info.Name, out var parsedByName)
                .Should().BeTrue($"Name '{info.Name}' should map to a ServiceType enum value");

            info.Value.Should().Be((int)parsedByName);

            returnedServices.Add(parsedByName);

            var expectedDescription = parsedByName switch
            {
                ServiceType.GeoIP => "Geographic location and ISP information",
                ServiceType.Ping => "Network reachability and latency check",
                ServiceType.RDAP => "Registration data via RDAP protocol",
                ServiceType.ReverseDNS => "Reverse DNS lookup (PTR record)",
                _ => "Unknown service"
            };

            info.Description.Should().Be(expectedDescription);
        }

        returnedServices.Should().BeEquivalentTo(expectedServices);
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

    private static GetJobStatus CreateGetStatusUseCase(IJobRepository jobRepository)
    {
        var sagaRepo = CreateTaskFriendlyMock<ISagaStateRepository>().Object;
        var workerReader = CreateTaskFriendlyMock<IWorkerResultReader>().Object;
        var useCaseLogger = new Mock<ILogger<GetJobStatus>>().Object;

        return new GetJobStatus(jobRepository, sagaRepo, workerReader, useCaseLogger);
    }

    /// <summary>
    /// Creates a loose mock that won't return null Tasks for unexpected invocations.
    /// This prevents "await null" failures when the system under test calls async dependencies
    /// we don't care about in a controller-level test.
    /// </summary>
    private static Mock<T> CreateTaskFriendlyMock<T>() where T : class
    {
        var mock = new Mock<T>(MockBehavior.Loose)
        {
            DefaultValueProvider = new TaskFriendlyDefaultValueProvider()
        };
        return mock;
    }

    private static string? GetAnonymousValue(object? values, string name)
    {
        if (values == null) return null;

        // Handle RouteValueDictionary / dictionaries (common in MVC routing)
        if (values is RouteValueDictionary rvd && rvd.TryGetValue(name, out var rvdValue))
            return rvdValue?.ToString();

        if (values is IDictionary<string, object?> dict && dict.TryGetValue(name, out var dictValue))
            return dictValue?.ToString();

        // Fallback: anonymous object property lookup
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

    /// <summary>
    /// Moq returns null for unexpected invocations on async members by default (Task / Task{T}),
    /// which can cause "await null" runtime failures.
    ///
    /// This DefaultValueProvider returns completed Tasks with sensible defaults, and empty collections
    /// for common enumerable types, making loose mocks safe for controller-level tests.
    /// </summary>
    private sealed class TaskFriendlyDefaultValueProvider : DefaultValueProvider
    {
        protected override object GetDefaultValue(Type type, Mock mock)
        {
            if (type == typeof(Task))
                return Task.CompletedTask;

            if (type == typeof(ValueTask))
                return default(ValueTask);

            if (type.IsGenericType)
            {
                var genDef = type.GetGenericTypeDefinition();

                if (genDef == typeof(Task<>))
                {
                    var resultType = type.GetGenericArguments()[0];
                    var defaultResult = GetDefaultNonTaskValue(resultType);

                    var fromResult = typeof(Task)
                        .GetMethod(nameof(Task.FromResult))!
                        .MakeGenericMethod(resultType);

                    return fromResult.Invoke(null, new[] { defaultResult })!;
                }

                if (genDef == typeof(ValueTask<>))
                {
                    var resultType = type.GetGenericArguments()[0];
                    var defaultResult = GetDefaultNonTaskValue(resultType);
                    return Activator.CreateInstance(type, defaultResult)!;
                }
            }

            return GetDefaultNonTaskValue(type)!;
        }

        private static object? GetDefaultNonTaskValue(Type type)
        {
            if (type == typeof(string))
                return string.Empty;

            if (type.IsArray)
                return Array.CreateInstance(type.GetElementType()!, 0);

            if (type.IsGenericType)
            {
                var def = type.GetGenericTypeDefinition();
                var args = type.GetGenericArguments();

                // Return empty array for common enumerable interfaces
                if (def == typeof(IEnumerable<>)
                    || def == typeof(IReadOnlyCollection<>)
                    || def == typeof(IReadOnlyList<>)
                    || def == typeof(ICollection<>)
                    || def == typeof(IList<>))
                {
                    return Array.CreateInstance(args[0], 0);
                }

                // Concrete list types
                if (def == typeof(List<>))
                {
                    return Activator.CreateInstance(type);
                }

                // Dictionaries
                if (def == typeof(IDictionary<,>)
                    || def == typeof(IReadOnlyDictionary<,>)
                    || def == typeof(Dictionary<,>))
                {
                    return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args));
                }

                // Sets
                if (def == typeof(ISet<>) || def == typeof(HashSet<>))
                {
                    return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(args));
                }
            }

            if (type.IsValueType)
                return Activator.CreateInstance(type);

            // Try parameterless ctor for classes; otherwise null
            if (!type.IsAbstract && type.GetConstructor(Type.EmptyTypes) != null)
                return Activator.CreateInstance(type);

            return null;
        }
    }
}
