using System.Diagnostics;
using System.Text.Json;
using DistributedLookup.Contracts;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using MassTransit;
using Microsoft.Extensions.Logging;

namespace DistributedLookup.Application.Workers;

/// <summary>
/// Abstract base class for lookup workers that consume commands and perform distributed lookups.
/// Implements common patterns: timing, result persistence, event publishing, and error handling.
/// 
/// Key design change: Workers now use IWorkerResultStore instead of IJobRepository.
/// - Workers do NOT touch saga state
/// - Workers store results independently via IWorkerResultStore
/// - Workers publish TaskCompleted with ResultLocation (WHERE data is stored, not the data itself)
/// </summary>
/// <typeparam name="TCommand">The command type this worker consumes (must implement ILookupCommand)</typeparam>
public abstract class LookupWorkerBase<TCommand>(ILogger logger, IWorkerResultStore resultStore) : IConsumer<TCommand> 
    where TCommand : class, ILookupCommand
{
    protected readonly ILogger Logger = logger;
    protected readonly IWorkerResultStore ResultStore = resultStore;
    
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
        var jobId = context.Message.JobId;
        
        Logger.LogInformation("Processing {ServiceType} lookup for job {JobId}, target: {Target}",
            ServiceType, jobId, context.Message.Target);

        ResultLocation? resultLocation = null;
        bool success = false;
        string? errorMessage = null;

        try
        {
            // Validate if this service can handle the target type
            var validationError = ValidateTarget(context.Message);
            if (validationError != null)
            {
                sw.Stop();
                errorMessage = validationError;
                resultLocation = await SaveFailureToStore(jobId, errorMessage, sw.Elapsed, context.CancellationToken);
            }
            else
            {
                // Derived class implements the actual lookup logic
                var result = await PerformLookupAsync(context.Message, context.CancellationToken);
                sw.Stop();

                // Store result via IWorkerResultStore (NOT IJobRepository)
                var jsonDoc = JsonSerializer.SerializeToDocument(result);
                resultLocation = await ResultStore.SaveResultAsync(jobId, ServiceType, jsonDoc, sw.Elapsed, context.CancellationToken);
                success = true;

                Logger.LogInformation("{ServiceType} lookup completed for job {JobId} in {Duration}ms",
                    ServiceType, jobId, sw.ElapsedMilliseconds);
            }
        }
        catch (Exception ex)
        {
            sw.Stop();
            errorMessage = ex.Message;
            Logger.LogError(ex, "Error processing {ServiceType} lookup for job {JobId}", ServiceType, jobId);
            
            try
            {
                resultLocation = await SaveFailureToStore(jobId, errorMessage, sw.Elapsed, context.CancellationToken);
            }
            catch (Exception storeEx)
            {
                Logger.LogError(storeEx, "Failed to store failure result for job {JobId}", jobId);
            }
        }

        // Publish TaskCompleted with ResultLocation (WHERE data is stored, not the data itself)
        await context.Publish(new TaskCompleted
        {
            JobId = jobId,
            ServiceType = ServiceType,
            Success = success,
            ErrorMessage = errorMessage,
            Duration = sw.Elapsed,
            Timestamp = DateTime.UtcNow,
            ResultLocation = resultLocation
        }, context.CancellationToken);
    }
    
    private async Task<ResultLocation> SaveFailureToStore(string jobId, string error, TimeSpan duration, CancellationToken cancellationToken)
    {
        return await ResultStore.SaveFailureAsync(jobId, ServiceType, error, duration, cancellationToken);
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
}