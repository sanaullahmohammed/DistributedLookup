using System.Diagnostics;
using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DistributedLookup.Application.Workers;

/// <summary>
/// Abstract base class for lookup workers that consume commands and perform distributed lookups.
/// Implements common patterns: timing, result persistence, event publishing, and error handling.
/// </summary>
/// <typeparam name="TCommand">The command type this worker consumes (must implement ILookupCommand)</typeparam>
public abstract class LookupWorkerBase<TCommand>(ILogger logger, IJobRepository repository) : IConsumer<TCommand> 
    where TCommand : class, ILookupCommand
{
    protected readonly ILogger Logger = logger;
    protected readonly IJobRepository Repository = repository;
    
    /// <summary>
    /// The service type this worker handles (e.g., GeoIP, Ping, RDAP, ReverseDNS)
    /// </summary>
    protected abstract ServiceType ServiceType { get; }

    /// <summary>
    /// Template method that defines the workflow for consuming lookup commands.
    /// Handles timing, validation, result persistence, and event publishing.
    /// </summary>
    public async Task Consume(ConsumeContext<TCommand> context)
    {
        var sw = Stopwatch.StartNew();
        Logger.LogInformation("Processing {ServiceType} lookup for job {JobId}, target: {Target}",
            ServiceType, context.Message.JobId, context.Message.Target);

        try
        {
            // Validate if this service can handle the target type
            var validationError = ValidateTarget(context.Message);
            if (validationError != null)
            {
                sw.Stop();
                await SaveAndPublishFailure(context, validationError, sw.Elapsed);
                return;
            }

            // Derived class implements the actual lookup logic
            var result = await PerformLookupAsync(context.Message, context.CancellationToken);
            sw.Stop();

            // Save and publish success
            await SaveResult(context.Message.JobId, ServiceType, result, sw.Elapsed);
            await PublishSuccess(context, result, sw.Elapsed);

            Logger.LogInformation("{ServiceType} lookup completed for job {JobId} in {Duration}ms",
                ServiceType, context.Message.JobId, sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            Logger.LogError(ex, "Error processing {ServiceType} lookup for job {JobId}", 
                ServiceType, context.Message.JobId);
            await SaveAndPublishFailure(context, ex.Message, sw.Elapsed);
        }
    }

    /// <summary>
    /// Override to implement the actual lookup logic for this service type.
    /// Throw exceptions for errors - they will be caught and handled by the base class.
    /// </summary>
    /// <param name="command">The command containing lookup parameters</param>
    /// <param name="cancellationToken">Cancellation token</param>
    /// <returns>The lookup result object (will be JSON serialized)</returns>
    protected abstract Task<object> PerformLookupAsync(TCommand command, CancellationToken cancellationToken);

    /// <summary>
    /// Override to validate if this service can process the target type.
    /// Return null if valid, error message if invalid.
    /// </summary>
    /// <param name="command">The command to validate</param>
    /// <returns>Null if valid, error message string if invalid</returns>
    protected virtual string? ValidateTarget(TCommand command) => null;

    /// <summary>
    /// Saves the successful lookup result to the repository
    /// </summary>
    private async Task SaveResult(string jobId, ServiceType serviceType, object data, TimeSpan duration)
    {
        var job = await Repository.GetByIdAsync(jobId);
        if (job != null)
        {
            var serializedData = JsonSerializer.Serialize(data);
            var result = ServiceResult.CreateSuccess(serviceType, serializedData, duration);
            job.AddResult(serviceType, result);
            await Repository.SaveAsync(job);
            Logger.LogDebug("Saved {ServiceType} result to repository for job {JobId}", serviceType, jobId);
        }
        else
        {
            Logger.LogWarning("Job {JobId} not found in repository", jobId);
        }
    }

    /// <summary>
    /// Publishes a TaskCompleted event for successful lookup
    /// </summary>
    private async Task PublishSuccess(ConsumeContext<TCommand> context, object data, TimeSpan duration)
    {
        var serializedData = JsonSerializer.Serialize(data);
        await context.Publish(new TaskCompleted
        {
            JobId = context.Message.JobId,
            ServiceType = ServiceType,
            Success = true,
            Data = serializedData,
            Duration = duration
        });
    }

    /// <summary>
    /// Saves failure to repository and publishes TaskCompleted event with error
    /// </summary>
    private async Task SaveAndPublishFailure(ConsumeContext<TCommand> context, string error, TimeSpan duration)
    {
        // Persist failure to repository
        var job = await Repository.GetByIdAsync(context.Message.JobId);
        if (job != null)
        {
            var result = ServiceResult.CreateFailure(ServiceType, error, duration);
            job.AddResult(ServiceType, result);
            await Repository.SaveAsync(job);
        }

        // Notify saga
        await context.Publish(new TaskCompleted
        {
            JobId = context.Message.JobId,
            ServiceType = ServiceType,
            Success = false,
            ErrorMessage = error,
            Duration = duration
        });
    }
}