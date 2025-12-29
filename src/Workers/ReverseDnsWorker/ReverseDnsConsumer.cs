using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using MassTransit;

namespace DistributedLookup.Workers.ReverseDNS;

/// <summary>
/// Consumer that processes reverse DNS (PTR) lookup commands.
/// Persists results directly to the repository and publishes <see cref="TaskCompleted"/>.
/// </summary>
public sealed class ReverseDnsConsumer : IConsumer<CheckReverseDNS>
{
    private readonly ILogger<ReverseDnsConsumer> _logger;
    private readonly IJobRepository _repository;

    public ReverseDnsConsumer(ILogger<ReverseDnsConsumer> logger, IJobRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    public async Task Consume(ConsumeContext<CheckReverseDNS> context)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Processing reverse DNS lookup for job {JobId}, target: {Target}",
            context.Message.JobId, context.Message.Target);

        try
        {
            // Reverse DNS is meaningful for IP targets only
            if (context.Message.TargetType != LookupTarget.IPAddress)
            {
                sw.Stop();
                await SaveAndPublishFailure(context, "Reverse DNS lookup requires an IP address target.", sw.Elapsed);
                return;
            }

            if (!IPAddress.TryParse(context.Message.Target, out var ip))
            {
                sw.Stop();
                await SaveAndPublishFailure(context, $"Invalid IP address: {context.Message.Target}", sw.Elapsed);
                return;
            }

            // DNS resolution can block depending on the resolver; apply a soft timeout.
            var timeout = TimeSpan.FromSeconds(5);

            var lookupTask = Dns.GetHostEntryAsync(ip);
            var completed = await Task.WhenAny(lookupTask, Task.Delay(timeout, context.CancellationToken));

            if (completed != lookupTask)
            {
                sw.Stop();
                await SaveAndPublishFailure(context, $"Reverse DNS lookup timed out after {timeout.TotalSeconds:0}s.", sw.Elapsed);
                return;
            }

            // If GetHostEntryAsync faulted, this await will throw.
            IPHostEntry entry;
            try
            {
                entry = await lookupTask;
            }
            catch (SocketException se) when (se.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
            {
                // Common outcome: no PTR record exists.
                sw.Stop();

                var notFound = new ReverseDnsLookupResult
                {
                    Input = context.Message.Target,
                    Found = false,
                    HostName = null,
                    Aliases = Array.Empty<string>(),
                    Addresses = Array.Empty<string>(),
                    QueriedAtUtc = DateTimeOffset.UtcNow
                };

                var rawNotFound = JsonSerializer.Serialize(notFound);

                await SaveResult(context.Message.JobId, ServiceType.ReverseDNS, rawNotFound, sw.Elapsed);

                _logger.LogInformation("No PTR record found for {Target} (job {JobId})",
                    context.Message.Target, context.Message.JobId);

                await context.Publish(new TaskCompleted
                {
                    JobId = context.Message.JobId,
                    ServiceType = ServiceType.ReverseDNS,
                    Success = true,
                    Data = rawNotFound,
                    Duration = sw.Elapsed
                });

                return;
            }

            sw.Stop();

            var result = new ReverseDnsLookupResult
            {
                Input = context.Message.Target,
                Found = !string.IsNullOrWhiteSpace(entry.HostName),
                HostName = entry.HostName,
                Aliases = entry.Aliases ?? Array.Empty<string>(),
                Addresses = entry.AddressList?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
                QueriedAtUtc = DateTimeOffset.UtcNow
            };

            var rawData = JsonSerializer.Serialize(result);

            // Persist result to repository
            await SaveResult(context.Message.JobId, ServiceType.ReverseDNS, rawData, sw.Elapsed);

            _logger.LogInformation("Reverse DNS lookup completed for job {JobId} in {Duration}ms",
                context.Message.JobId, sw.ElapsedMilliseconds);

            // Notify saga
            await context.Publish(new TaskCompleted
            {
                JobId = context.Message.JobId,
                ServiceType = ServiceType.ReverseDNS,
                Success = true,
                Data = rawData,
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Error processing reverse DNS lookup for job {JobId}", context.Message.JobId);

            await SaveAndPublishFailure(context, ex.Message, sw.Elapsed);
        }
    }

    private async Task SaveResult(string jobId, ServiceType serviceType, object data, TimeSpan duration)
    {
        var job = await _repository.GetByIdAsync(jobId);
        if (job != null)
        {
            var result = ServiceResult.CreateSuccess(serviceType, data, duration);
            job.AddResult(serviceType, result);
            await _repository.SaveAsync(job);
            _logger.LogInformation("Saved reverse DNS result to repository for job {JobId}", jobId);
        }
        else
        {
            _logger.LogWarning("Job {JobId} not found in repository", jobId);
        }
    }

    private async Task SaveAndPublishFailure(ConsumeContext<CheckReverseDNS> context, string error, TimeSpan duration)
    {
        // Persist failure to repository
        var job = await _repository.GetByIdAsync(context.Message.JobId);
        if (job != null)
        {
            var result = ServiceResult.CreateFailure(ServiceType.ReverseDNS, error, duration);
            job.AddResult(ServiceType.ReverseDNS, result);
            await _repository.SaveAsync(job);
        }

        // Notify saga
        await context.Publish(new TaskCompleted
        {
            JobId = context.Message.JobId,
            ServiceType = ServiceType.ReverseDNS,
            Success = false,
            ErrorMessage = error,
            Duration = duration
        });
    }

    private sealed class ReverseDnsLookupResult
    {
        public string Input { get; init; } = string.Empty;
        public bool Found { get; init; }
        public string? HostName { get; init; }
        public string[] Aliases { get; init; } = Array.Empty<string>();
        public string[] Addresses { get; init; } = Array.Empty<string>();
        public DateTimeOffset QueriedAtUtc { get; init; }
    }
}
