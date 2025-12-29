using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DistributedLookup.Application.Saga;

public class LookupJobStateMachine : MassTransitStateMachine<LookupJobState>
{
    public State Processing { get; private set; } = null!;
    public State Completed { get; private set; } = null!;

    public Event<JobSubmitted> JobSubmitted { get; private set; } = null!;
    public Event<TaskCompleted> TaskCompleted { get; private set; } = null!;

    public LookupJobStateMachine(ILogger<LookupJobStateMachine> logger)
    {
        InstanceState(x => x.CurrentState);

        Event(() => JobSubmitted, x => x.CorrelateById(c => Guid.Parse(c.Message.JobId)));
        Event(() => TaskCompleted, x => x.CorrelateById(c => Guid.Parse(c.Message.JobId)));

        Initially(
            When(JobSubmitted)
                .Then(context =>
                {
                    context.Saga.JobId = context.Message.JobId;
                    context.Saga.PendingServices = context.Message.Services.ToList();
                    context.Saga.CreatedAt = DateTime.UtcNow;
                })
                .ThenAsync(async context =>
                {
                    logger.LogInformation("Dispatching {Count} commands for Job {JobId}", 
                        context.Message.Services.Length, context.Message.JobId);

                    var publishTasks = context.Message.Services
                        .Select(service => CreateCommand(service, context.Message))
                        .Select(cmd => context.Publish(cmd, cmd.GetType()));

                    await Task.WhenAll(publishTasks);
                })
                .TransitionTo(Processing)
        );

        During(Processing,
            When(TaskCompleted)
                .Then(context =>
                {
                    if (context.Saga.PendingServices.Remove(context.Message.ServiceType))
                    {
                        context.Saga.CompletedServices.Add(context.Message.ServiceType);
                        logger.LogDebug("Job {JobId}: Received {Service}. Pending: {Pending}", 
                            context.Saga.JobId, context.Message.ServiceType, context.Saga.PendingServices.Count);
                    }
                })
                .If(context => context.Saga.PendingServices.Count == 0,
                    binder => binder
                        .Then(context => 
                        {
                            context.Saga.CompletedAt = DateTime.UtcNow;
                            logger.LogInformation("Job {JobId} Completed successfully.", context.Saga.JobId);
                        })
                        .TransitionTo(Completed)
                        .Finalize()
                )
        );

        SetCompletedWhenFinalized();
    }

    // Helper to keep the Saga clean and avoid allocations
    private static ILookupCommand CreateCommand(ServiceType service, JobSubmitted msg)
    {
        return service switch
        {
            ServiceType.GeoIP => new CheckGeoIP { JobId = msg.JobId, Target = msg.Target, TargetType = msg.TargetType },
            ServiceType.Ping => new CheckPing { JobId = msg.JobId, Target = msg.Target, TargetType = msg.TargetType },
            ServiceType.RDAP => new CheckRDAP { JobId = msg.JobId, Target = msg.Target, TargetType = msg.TargetType },
            ServiceType.ReverseDNS => new CheckReverseDNS { JobId = msg.JobId, Target = msg.Target, TargetType = msg.TargetType },
            _ => throw new ArgumentException($"Unknown service type: {service}")
        };
    }
}