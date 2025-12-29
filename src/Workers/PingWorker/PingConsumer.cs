using System.Diagnostics;
using System.Net.NetworkInformation;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using MassTransit;

namespace DistributedLookup.Workers.Ping;

/// <summary>
/// Consumer that processes Ping commands.
/// Runs in a separate process/container as a worker.
/// Persists results directly to the repository.
/// </summary>
public class PingConsumer : IConsumer<CheckPing>
{
    private readonly ILogger<PingConsumer> _logger;
    private readonly IJobRepository _repository;

    public PingConsumer(ILogger<PingConsumer> logger, IJobRepository repository)
    {
        _logger = logger;
        _repository = repository;
    }

    public async Task Consume(ConsumeContext<CheckPing> context)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Processing Ping check for job {JobId}, target: {Target}",
            context.Message.JobId, context.Message.Target);

        try
        {
            using var pinger = new System.Net.NetworkInformation.Ping();
            var results = new List<PingResult>();

            // Send 4 pings
            for (int i = 0; i < 4; i++)
            {
                var reply = await pinger.SendPingAsync(context.Message.Target, 5000);
                results.Add(new PingResult
                {
                    Success = reply.Status == IPStatus.Success,
                    RoundtripTime = reply.RoundtripTime,
                    Status = reply.Status.ToString(),
                    TTL = reply.Options?.Ttl ?? 0
                });

                if (i < 3) await Task.Delay(500); // Wait between pings
            }

            sw.Stop();

            var successful = results.Count(r => r.Success);
            var avgRtt = results.Where(r => r.Success).Select(r => r.RoundtripTime).DefaultIfEmpty(0).Average();

            var data = new PingResponse
            {
                Target = context.Message.Target,
                PacketsSent = results.Count,
                PacketsReceived = successful,
                PacketLoss = ((results.Count - successful) * 100.0 / results.Count),
                AverageRoundtripMs = avgRtt,
                MinRoundtripMs = results.Where(r => r.Success).Select(r => r.RoundtripTime).DefaultIfEmpty(0).Min(),
                MaxRoundtripMs = results.Where(r => r.Success).Select(r => r.RoundtripTime).DefaultIfEmpty(0).Max(),
                Results = results
            };

            // Persist result to repository
            await SaveResult(context.Message.JobId, ServiceType.Ping, data, sw.Elapsed);

            _logger.LogInformation("Ping check completed for job {JobId}: {Received}/{Sent} packets, avg {Avg}ms",
                context.Message.JobId, successful, results.Count, avgRtt);

            // Notify saga
            await context.Publish(new TaskCompleted
            {
                JobId = context.Message.JobId,
                ServiceType = ServiceType.Ping,
                Success = true,
                Data = System.Text.Json.JsonSerializer.Serialize(data),
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Error processing Ping check for job {JobId}", context.Message.JobId);
            
            // Persist failure to repository
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
            _logger.LogInformation("Saved Ping result to repository for job {JobId}", jobId);
        }
        else
        {
            _logger.LogWarning("Job {JobId} not found in repository", jobId);
        }
    }

    private async Task SaveAndPublishFailure(ConsumeContext<CheckPing> context, string error, TimeSpan duration)
    {
        // Persist failure to repository
        var job = await _repository.GetByIdAsync(context.Message.JobId);
        if (job != null)
        {
            var result = ServiceResult.CreateFailure(ServiceType.Ping, error, duration);
            job.AddResult(ServiceType.Ping, result);
            await _repository.SaveAsync(job);
        }

        // Notify saga
        await context.Publish(new TaskCompleted
        {
            JobId = context.Message.JobId,
            ServiceType = ServiceType.Ping,
            Success = false,
            ErrorMessage = error,
            Duration = duration
        });
    }

    private class PingResult
    {
        public bool Success { get; set; }
        public long RoundtripTime { get; set; }
        public string Status { get; set; } = string.Empty;
        public int TTL { get; set; }
    }

    private class PingResponse
    {
        public string Target { get; set; } = string.Empty;
        public int PacketsSent { get; set; }
        public int PacketsReceived { get; set; }
        public double PacketLoss { get; set; }
        public double AverageRoundtripMs { get; set; }
        public double MinRoundtripMs { get; set; }
        public double MaxRoundtripMs { get; set; }
        public List<PingResult> Results { get; set; } = new();
    }
}
