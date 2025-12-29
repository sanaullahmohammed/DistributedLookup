using System.Diagnostics;
using System.Net.Http.Json;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using MassTransit;

namespace DistributedLookup.Workers.Geo;

/// <summary>
/// Consumer that processes GeoIP lookup commands.
/// Runs in a separate process/container as a worker.
/// Persists results directly to the repository.
/// </summary>
public class GeoIPConsumer : IConsumer<CheckGeoIP>
{
    private readonly ILogger<GeoIPConsumer> _logger;
    private readonly HttpClient _httpClient;
    private readonly IJobRepository _repository;

    public GeoIPConsumer(ILogger<GeoIPConsumer> logger, HttpClient httpClient, IJobRepository repository)
    {
        _logger = logger;
        _httpClient = httpClient;
        _repository = repository;
    }

    public async Task Consume(ConsumeContext<CheckGeoIP> context)
    {
        var sw = Stopwatch.StartNew();
        _logger.LogInformation("Processing GeoIP lookup for job {JobId}, target: {Target}",
            context.Message.JobId, context.Message.Target);

        try
        {
            // Resolve IP address if domain
            var ipAddress = context.Message.TargetType == LookupTarget.Domain
                ? await ResolveToIp(context.Message.Target)
                : context.Message.Target;

            if (ipAddress == null)
            {
                await SaveAndPublishFailure(context, "Failed to resolve domain to IP", sw.Elapsed);
                return;
            }

            // Use free GeoIP API (ip-api.com)
            var url = $"http://ip-api.com/json/{ipAddress}?fields=status,message,country,countryCode,region,regionName,city,zip,lat,lon,timezone,isp,org,as";
            var response = await _httpClient.GetAsync(url);

            if (!response.IsSuccessStatusCode)
            {
                await SaveAndPublishFailure(context, $"GeoIP API returned {response.StatusCode}", sw.Elapsed);
                return;
            }

            var data = await response.Content.ReadFromJsonAsync<GeoIPResponse>();
            
            if (data?.Status != "success")
            {
                await SaveAndPublishFailure(context, data?.Message ?? "Unknown error", sw.Elapsed);
                return;
            }

            sw.Stop();
            
            // Persist result to repository
            await SaveResult(context.Message.JobId, ServiceType.GeoIP, data, sw.Elapsed);

            _logger.LogInformation("GeoIP lookup completed for job {JobId} in {Duration}ms",
                context.Message.JobId, sw.ElapsedMilliseconds);

            // Notify saga
            await context.Publish(new TaskCompleted
            {
                JobId = context.Message.JobId,
                ServiceType = ServiceType.GeoIP,
                Success = true,
                Data = System.Text.Json.JsonSerializer.Serialize(data),
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error processing GeoIP lookup for job {JobId}", context.Message.JobId);
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
            _logger.LogInformation("Saved GeoIP result to repository for job {JobId}", jobId);
        }
        else
        {
            _logger.LogWarning("Job {JobId} not found in repository", jobId);
        }
    }

    private async Task SaveAndPublishFailure(ConsumeContext<CheckGeoIP> context, string error, TimeSpan duration)
    {
        // Persist failure to repository
        var job = await _repository.GetByIdAsync(context.Message.JobId);
        if (job != null)
        {
            var result = ServiceResult.CreateFailure(ServiceType.GeoIP, error, duration);
            job.AddResult(ServiceType.GeoIP, result);
            await _repository.SaveAsync(job);
        }

        // Notify saga
        await context.Publish(new TaskCompleted
        {
            JobId = context.Message.JobId,
            ServiceType = ServiceType.GeoIP,
            Success = false,
            ErrorMessage = error,
            Duration = duration
        });
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
