using DistributedLookup.Application.Saga;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
using MassTransit;
using MassTransit.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace Tests.Application.Saga;

public class LookupJobStateMachineTests
{
    private static ServiceProvider BuildProvider(Mock<ILogger<LookupJobStateMachine>> logger)
    {
        return new ServiceCollection()
            .AddSingleton(logger.Object)
            .AddMassTransitTestHarness(cfg =>
            {
                cfg.SetTestTimeouts(
                    testTimeout: TimeSpan.FromSeconds(10),
                    testInactivityTimeout: TimeSpan.FromSeconds(2));

                cfg.AddSagaStateMachine<LookupJobStateMachine, LookupJobState>();
            })
            .BuildServiceProvider(true);
    }

    [Fact]
    public async Task JobSubmitted_ShouldCreateSagaInProcessing_AndPublishCommands()
    {
        var logger = new Mock<ILogger<LookupJobStateMachine>>();

        await using var provider = BuildProvider(logger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var jobId = Guid.NewGuid().ToString(); // must be Guid-parseable due to CorrelateById(Guid.Parse(...))
            var correlationId = Guid.Parse(jobId);

            var target = "8.8.8.8";
            var targetType = LookupTarget.IPAddress;

            var services = new[]
            {
                ServiceType.GeoIP,
                ServiceType.Ping,
                ServiceType.RDAP,
                ServiceType.ReverseDNS
            };

            await harness.Bus.Publish<JobSubmitted>(new
            {
                JobId = jobId,
                Target = target,
                TargetType = targetType,
                Services = services
            });

            var sagaHarness = harness.GetSagaStateMachineHarness<LookupJobStateMachine, LookupJobState>();

            // Wait until saga is created in Processing (avoids race)
            (await sagaHarness.Exists(correlationId, x => x.Processing)).Should().NotBeNull();

            var instance = sagaHarness.Sagas.Contains(correlationId);
            instance.Should().NotBeNull();

            instance!.JobId.Should().Be(jobId);
            instance.PendingServices.Should().BeEquivalentTo(services);
            instance.CompletedServices.Should().BeEmpty();
            instance.CompletedAt.Should().BeNull();

            // One command per service
            (await harness.Published.Any<CheckGeoIP>()).Should().BeTrue();
            (await harness.Published.Any<CheckPing>()).Should().BeTrue();
            (await harness.Published.Any<CheckRDAP>()).Should().BeTrue();
            (await harness.Published.Any<CheckReverseDNS>()).Should().BeTrue();

            // Spot-check payload for one of them
            var geo = await harness.Published.SelectAsync<CheckGeoIP>().FirstOrDefault();
            geo.Should().NotBeNull();
            geo!.Context.Message.JobId.Should().Be(jobId);
            geo.Context.Message.Target.Should().Be(target);
            geo.Context.Message.TargetType.Should().Be(targetType);

            VerifyLoggerContains(logger, LogLevel.Information, "Dispatching");
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task TaskCompleted_ShouldMoveService_FromPending_ToCompleted_WhileProcessing()
    {
        var logger = new Mock<ILogger<LookupJobStateMachine>>();

        await using var provider = BuildProvider(logger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var jobId = Guid.NewGuid().ToString();
            var correlationId = Guid.Parse(jobId);

            var services = new[] { ServiceType.GeoIP, ServiceType.Ping };

            await harness.Bus.Publish<JobSubmitted>(new
            {
                JobId = jobId,
                Target = "8.8.8.8",
                TargetType = LookupTarget.IPAddress,
                Services = services
            });

            var sagaHarness = harness.GetSagaStateMachineHarness<LookupJobStateMachine, LookupJobState>();
            (await sagaHarness.Exists(correlationId, x => x.Processing)).Should().NotBeNull();

            await harness.Bus.Publish<TaskCompleted>(new
            {
                JobId = jobId,
                ServiceType = ServiceType.GeoIP
            });

            (await sagaHarness.Consumed.Any<TaskCompleted>()).Should().BeTrue();

            var instance = sagaHarness.Sagas.Contains(correlationId);
            instance.Should().NotBeNull();

            instance!.CurrentState.Should().Be(sagaHarness.StateMachine.Processing.Name);
            instance.PendingServices.Should().BeEquivalentTo(new[] { ServiceType.Ping });
            instance.CompletedServices.Should().BeEquivalentTo(new[] { ServiceType.GeoIP });
            instance.CompletedAt.Should().BeNull();
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task TaskCompleted_WhenLastServiceCompletes_ShouldFinalize_AndSetCompletedAt()
    {
        var logger = new Mock<ILogger<LookupJobStateMachine>>();

        await using var provider = BuildProvider(logger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var jobId = Guid.NewGuid().ToString();
            var correlationId = Guid.Parse(jobId);

            var services = new[] { ServiceType.GeoIP, ServiceType.Ping };

            await harness.Bus.Publish<JobSubmitted>(new
            {
                JobId = jobId,
                Target = "8.8.8.8",
                TargetType = LookupTarget.IPAddress,
                Services = services
            });

            var sagaHarness = harness.GetSagaStateMachineHarness<LookupJobStateMachine, LookupJobState>();
            (await sagaHarness.Exists(correlationId, x => x.Processing)).Should().NotBeNull();

            var before = DateTime.UtcNow;

            await harness.Bus.Publish<TaskCompleted>(new { JobId = jobId, ServiceType = ServiceType.GeoIP });
            await harness.Bus.Publish<TaskCompleted>(new { JobId = jobId, ServiceType = ServiceType.Ping });

            // Wait until either:
            // - saga is in Final, OR
            // - saga is removed (some repositories remove finalized sagas quickly)
            var finalInstance = await WaitForFinalOrRemoval(sagaHarness, correlationId, TimeSpan.FromSeconds(10));

            // We can always assert the completion log occurred (proves the completion branch executed)
            VerifyLoggerContains(logger, LogLevel.Information, "Completed successfully");

            if (finalInstance is null)
            {
                // Removed == finalized
                sagaHarness.Sagas.Contains(correlationId).Should().BeNull();
            }
            else
            {
                finalInstance.CurrentState.Should().Be("Final");
                finalInstance.PendingServices.Should().BeEmpty();
                finalInstance.CompletedServices.Should().BeEquivalentTo(services);

                finalInstance.CompletedAt.Should().NotBeNull();
                finalInstance.CompletedAt!.Value.Should().BeOnOrAfter(before);
            }
        }
        finally
        {
            await harness.Stop();
        }
    }

    [Fact]
    public async Task JobSubmitted_WithUnknownServiceType_ShouldPublishFault()
    {
        var logger = new Mock<ILogger<LookupJobStateMachine>>();

        await using var provider = BuildProvider(logger);
        var harness = provider.GetRequiredService<ITestHarness>();
        await harness.Start();

        try
        {
            var jobId = Guid.NewGuid().ToString();

            await harness.Bus.Publish<JobSubmitted>(new
            {
                JobId = jobId,
                Target = "8.8.8.8",
                TargetType = LookupTarget.IPAddress,
                Services = new[] { (ServiceType)999 }
            });

            (await harness.Published.Any<Fault<JobSubmitted>>()).Should().BeTrue();
        }
        finally
        {
            await harness.Stop();
        }
    }

    private static async Task<LookupJobState?> WaitForFinalOrRemoval(
        ISagaStateMachineTestHarness<LookupJobStateMachine, LookupJobState> sagaHarness,
        Guid correlationId,
        TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;

        while (DateTime.UtcNow < deadline)
        {
            var instance = sagaHarness.Sagas.Contains(correlationId);

            // If it vanished, treat as finalized (repository removed it)
            if (instance is null)
                return null;

            if (string.Equals(instance.CurrentState, "Final", StringComparison.OrdinalIgnoreCase))
                return instance;

            await Task.Delay(50);
        }

        return sagaHarness.Sagas.Contains(correlationId);
    }

    private static void VerifyLoggerContains(
        Mock<ILogger<LookupJobStateMachine>> logger,
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
