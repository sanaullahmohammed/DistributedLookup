using System.Net.NetworkInformation;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Application.Workers;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Workers.PingWorker;

/// <summary>
/// Consumer that processes Ping commands.
/// Runs in a separate process/container as a worker.
/// </summary>
public sealed class PingConsumer(ILogger<PingConsumer> logger, IJobRepository repository) : LookupWorkerBase<CheckPing>(logger, repository)
{
    protected override ServiceType ServiceType => ServiceType.Ping;

    protected override async Task<object> PerformLookupAsync(CheckPing command, CancellationToken cancellationToken)
    {
        using var pinger = new System.Net.NetworkInformation.Ping();
        var results = new List<PingResult>();

        // Send 4 pings
        for (int i = 0; i < 4; i++)
        {
            var reply = await pinger.SendPingAsync(command.Target, 5000);
            results.Add(new PingResult
            {
                Success = reply.Status == IPStatus.Success,
                RoundtripTime = reply.RoundtripTime,
                Status = reply.Status.ToString(),
                TTL = reply.Options?.Ttl ?? 0
            });

            if (i < 3) await Task.Delay(500, cancellationToken); // Wait between pings
        }

        var successful = results.Count(r => r.Success);
        var avgRtt = results.Where(r => r.Success).Select(r => r.RoundtripTime).DefaultIfEmpty(0).Average();

        var data = new PingResponse
        {
            Target = command.Target,
            PacketsSent = results.Count,
            PacketsReceived = successful,
            PacketLoss = ((results.Count - successful) * 100.0 / results.Count),
            AverageRoundtripMs = avgRtt,
            MinRoundtripMs = results.Where(r => r.Success).Select(r => r.RoundtripTime).DefaultIfEmpty(0).Min(),
            MaxRoundtripMs = results.Where(r => r.Success).Select(r => r.RoundtripTime).DefaultIfEmpty(0).Max(),
            Results = results
        };

        Logger.LogInformation("Ping check completed for job {JobId}: {Received}/{Sent} packets, avg {Avg}ms",
            command.JobId, successful, results.Count, avgRtt);

        return data;
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