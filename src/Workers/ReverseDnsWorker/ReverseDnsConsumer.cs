using System.Net;
using System.Net.Sockets;
using DistributedLookup.Application.Workers;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Workers.ReverseDnsWorker;

/// <summary>
/// Consumer that processes reverse DNS (PTR) lookup commands.
/// Runs in a separate process/container as a worker.
/// Uses IWorkerResultStore for result storage.
/// </summary>
public sealed class ReverseDnsConsumer(ILogger<ReverseDnsConsumer> logger, IWorkerResultStore resultStore) : LookupWorkerBase<CheckReverseDNS>(logger, resultStore)
{
    protected override ServiceType ServiceType => ServiceType.ReverseDNS;

    protected override string? ValidateTarget(CheckReverseDNS command)
    {
        // Reverse DNS is meaningful for IP targets only
        if (command.TargetType != LookupTarget.IPAddress)
            return "Reverse DNS lookup requires an IP address target.";

        if (!IPAddress.TryParse(command.Target, out _))
            return $"Invalid IP address: {command.Target}";

        return null;
    }

    protected override async Task<object> PerformLookupAsync(CheckReverseDNS command, CancellationToken cancellationToken)
    {
        var ip = IPAddress.Parse(command.Target);
        var timeout = TimeSpan.FromSeconds(5);

        var lookupTask = Dns.GetHostEntryAsync(ip);
        var completed = await Task.WhenAny(lookupTask, Task.Delay(timeout, cancellationToken));

        if (completed != lookupTask)
            throw new TimeoutException($"Reverse DNS lookup timed out after {timeout.TotalSeconds:0}s.");

        IPHostEntry entry;
        try
        {
            entry = await lookupTask;
        }
        catch (SocketException se) when (se.SocketErrorCode is SocketError.HostNotFound or SocketError.NoData)
        {
            // Common outcome: no PTR record exists.
            Logger.LogInformation("No PTR record found for {Target} (job {JobId})",
                command.Target, command.JobId);

            return new ReverseDnsLookupResult
            {
                Input = command.Target,
                Found = false,
                HostName = null,
                Aliases = Array.Empty<string>(),
                Addresses = Array.Empty<string>(),
                QueriedAtUtc = DateTimeOffset.UtcNow
            };
        }

        return new ReverseDnsLookupResult
        {
            Input = command.Target,
            Found = !string.IsNullOrWhiteSpace(entry.HostName),
            HostName = entry.HostName,
            Aliases = entry.Aliases ?? Array.Empty<string>(),
            Addresses = entry.AddressList?.Select(a => a.ToString()).ToArray() ?? Array.Empty<string>(),
            QueriedAtUtc = DateTimeOffset.UtcNow
        };
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