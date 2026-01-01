using System.Net.Http.Json;
using DistributedLookup.Application.Workers;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Workers.GeoWorker;

/// <summary>
/// Consumer that processes GeoIP lookup commands.
/// Runs in a separate process/container as a worker.
/// Uses IWorkerResultStore for result storage.
/// </summary>
public sealed class GeoIPConsumer(ILogger<GeoIPConsumer> logger, HttpClient httpClient, IWorkerResultStore resultStore) : LookupWorkerBase<CheckGeoIP>(logger, resultStore)
{
    private readonly HttpClient _httpClient = httpClient;

    protected override ServiceType ServiceType => ServiceType.GeoIP;

    protected override async Task<object> PerformLookupAsync(CheckGeoIP command, CancellationToken cancellationToken)
    {
        // Resolve IP address if domain
        var ipAddress = command.TargetType == LookupTarget.Domain
            ? await ResolveToIp(command.Target)
            : command.Target;

        if (ipAddress == null)
            throw new InvalidOperationException("Failed to resolve domain to IP");

        // Use free GeoIP API (ip-api.com)
        var url = $"http://ip-api.com/json/{ipAddress}?fields=status,message,country,countryCode,region,regionName,city,zip,lat,lon,timezone,isp,org,as";
        var response = await _httpClient.GetAsync(url, cancellationToken);

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"GeoIP API returned {response.StatusCode}");

        var data = await response.Content.ReadFromJsonAsync<GeoIPResponse>(cancellationToken);
        
        if (data?.Status != "success")
            throw new InvalidOperationException(data?.Message ?? "Unknown error from GeoIP API");

        return data;
    }

    private async Task<string?> ResolveToIp(string domain)
    {
        try
        {
            var addresses = await System.Net.Dns.GetHostAddressesAsync(domain);
            return addresses.FirstOrDefault()?.ToString();
        }
        catch
        {
            return null;
        }
    }

    private class GeoIPResponse
    {
        public string Status { get; set; } = string.Empty;
        public string? Message { get; set; }
        public string Country { get; set; } = string.Empty;
        public string CountryCode { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string RegionName { get; set; } = string.Empty;
        public string City { get; set; } = string.Empty;
        public string Zip { get; set; } = string.Empty;
        public double Lat { get; set; }
        public double Lon { get; set; }
        public string Timezone { get; set; } = string.Empty;
        public string Isp { get; set; } = string.Empty;
        public string Org { get; set; } = string.Empty;
        public string As { get; set; } = string.Empty;
    }
}