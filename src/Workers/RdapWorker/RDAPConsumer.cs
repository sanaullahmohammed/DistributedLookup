using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Commands;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using MassTransit;

namespace DistributedLookup.Workers.Rdap;

public class RDAPConsumer : IConsumer<CheckRDAP>
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        WriteIndented = false // normalized JSON output
    };

    // IANA RDAP DNS bootstrap (TLD -> RDAP base URLs)
    private static readonly Uri IanaDnsBootstrapUri = new("https://data.iana.org/rdap/dns.json");
    private static readonly SemaphoreSlim DnsBootstrapLock = new(1, 1);
    private static Dictionary<string, string[]>? _dnsBootstrapByTld;
    private static DateTimeOffset _dnsBootstrapFetchedAt = DateTimeOffset.MinValue;
    private static readonly TimeSpan DnsBootstrapTtl = TimeSpan.FromDays(7);

    private readonly ILogger<RDAPConsumer> _logger;
    private readonly HttpClient _httpClient;
    private readonly IJobRepository _repository;

    public RDAPConsumer(ILogger<RDAPConsumer> logger, HttpClient httpClient, IJobRepository repository)
    {
        _logger = logger;
        _httpClient = httpClient;
        _repository = repository;
    }

    public async Task Consume(ConsumeContext<CheckRDAP> context)
    {
        var sw = Stopwatch.StartNew();

        _logger.LogInformation(
            "Processing RDAP lookup for job {JobId}, target: {Target}, targetType: {TargetType}",
            context.Message.JobId,
            context.Message.Target,
            context.Message.TargetType
        );

        try
        {
            string rdapUrl;
            string effectiveTargetForLookup = context.Message.Target;

            if (context.Message.TargetType == LookupTarget.IPAddress)
            {
                // (Unchanged) IP RDAP
                rdapUrl = $"https://rdap.arin.net/registry/ip/{Uri.EscapeDataString(context.Message.Target)}";
            }
            else
            {
                // Domain RDAP: normalize + reduce subdomains -> apex domain, then discover RDAP server by TLD
                var normalizedHost = NormalizeDomainTarget(context.Message.Target);
                var apexDomain = GetApexDomain(normalizedHost);

                effectiveTargetForLookup = apexDomain;

                if (!string.Equals(context.Message.Target, apexDomain, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.LogInformation(
                        "Normalized domain target from '{Raw}' to apex domain '{Apex}' for job {JobId}",
                        context.Message.Target,
                        apexDomain,
                        context.Message.JobId
                    );
                }

                var tld = GetTld(apexDomain);
                var baseUrl = await ResolveDomainRdapBaseUrlAsync(tld, context.CancellationToken);

                // Fallback to rdap.org if bootstrap lookup fails
                rdapUrl = baseUrl != null
                    ? $"{baseUrl.TrimEnd('/')}/domain/{Uri.EscapeDataString(apexDomain)}"
                    : $"https://rdap.org/domain/{Uri.EscapeDataString(apexDomain)}";
            }

            using var request = new HttpRequestMessage(HttpMethod.Get, rdapUrl);
            request.Headers.Accept.ParseAdd("application/rdap+json");

            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                context.CancellationToken
            );

            if (!response.IsSuccessStatusCode)
            {
                sw.Stop();
                await SaveAndPublishFailure(
                    context,
                    $"RDAP server returned {response.StatusCode} for '{effectiveTargetForLookup}'",
                    sw.Elapsed
                );
                return;
            }

            var rawData = await response.Content.ReadAsStringAsync(context.CancellationToken);

            // 1) Validate JSON and normalize formatting (removes newlines/pretty-print whitespace)
            using var doc = JsonDocument.Parse(rawData);
            var normalizedJson = JsonSerializer.Serialize(doc.RootElement, JsonOptions);

            // 2) OPTIONAL: extract a few fields (safe model) for logging/metrics without losing data
            RDAPResponse? parsed = null;
            try
            {
                parsed = JsonSerializer.Deserialize<RDAPResponse>(normalizedJson, JsonOptions);
            }
            catch
            {
                _logger.LogWarning(
                    "Failed to parse RDAP response into simplified model for job {JobId}",
                    context.Message.JobId
                );
            }

            sw.Stop();

            await SaveResult(context.Message.JobId, ServiceType.RDAP, normalizedJson, sw.Elapsed);

            _logger.LogInformation(
                "RDAP lookup completed for job {JobId} in {Duration}ms. " +
                "ObjectClass={ObjectClass}, Handle={Handle}, LdhName={LdhName}, Name={Name}, Range={Start}-{End}",
                context.Message.JobId,
                sw.ElapsedMilliseconds,
                parsed?.ObjectClassName,
                parsed?.Handle,
                parsed?.LdhName ?? parsed?.UnicodeName,
                parsed?.Name,
                parsed?.StartAddress,
                parsed?.EndAddress
            );

            await context.Publish(new TaskCompleted
            {
                JobId = context.Message.JobId,
                ServiceType = ServiceType.RDAP,
                Success = true,
                Data = normalizedJson,
                Duration = sw.Elapsed
            });
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogError(ex, "Error processing RDAP lookup for job {JobId}", context.Message.JobId);
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
            _logger.LogInformation("Saved RDAP result to repository for job {JobId}", jobId);
        }
        else
        {
            _logger.LogWarning("Job {JobId} not found in repository", jobId);
        }
    }

    private async Task SaveAndPublishFailure(ConsumeContext<CheckRDAP> context, string error, TimeSpan duration)
    {
        var job = await _repository.GetByIdAsync(context.Message.JobId);
        if (job != null)
        {
            var result = ServiceResult.CreateFailure(ServiceType.RDAP, error, duration);
            job.AddResult(ServiceType.RDAP, result);
            await _repository.SaveAsync(job);
        }

        await context.Publish(new TaskCompleted
        {
            JobId = context.Message.JobId,
            ServiceType = ServiceType.RDAP,
            Success = false,
            ErrorMessage = error,
            Duration = duration
        });
    }

    // ------------------------
    // Domain normalization helpers
    // ------------------------

    private static string NormalizeDomainTarget(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
            throw new ArgumentException("Domain target is empty.", nameof(input));

        var trimmed = input.Trim();

        // If caller ever passes a URL, extract host; otherwise treat as host.
        if (!Uri.TryCreate(trimmed, UriKind.Absolute, out var uri) &&
            Uri.TryCreate("http://" + trimmed, UriKind.Absolute, out var uriWithScheme))
        {
            uri = uriWithScheme;
        }

        var host = uri?.Host ?? trimmed;

        // remove trailing dot (FQDN form)
        host = host.Trim().TrimEnd('.');

        // normalize to ASCII (punycode) + lowercase
        var idn = new IdnMapping();
        host = idn.GetAscii(host).ToLowerInvariant();

        return host;
    }

    /// <summary>
    /// For your provided formats, this reduces:
    /// - www.google.com -> google.com
    /// - docs.google.com -> google.com
    /// - www.docs.google.com -> google.com
    ///
    /// NOTE: This uses "last 2 labels". If you need correct handling for cases like example.co.uk,
    /// swap this implementation to use a Public Suffix List library.
    /// </summary>
    private static string GetApexDomain(string host)
    {
        var labels = host.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (labels.Length < 2)
            return host;

        return $"{labels[^2]}.{labels[^1]}";
    }

    private static string GetTld(string hostOrDomain)
    {
        var labels = hostOrDomain.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return labels.Length == 0 ? hostOrDomain : labels[^1];
    }

    // ------------------------
    // IANA DNS bootstrap discovery + caching
    // ------------------------

    private async Task<string?> ResolveDomainRdapBaseUrlAsync(string tld, CancellationToken ct)
    {
        var bootstrap = await GetDnsBootstrapAsync(ct);
        if (bootstrap.TryGetValue(tld, out var urls) && urls.Length > 0)
            return urls[0];

        _logger.LogWarning("No RDAP bootstrap entry found for TLD '{Tld}'. Falling back to rdap.org.", tld);
        return null;
    }

    private async Task<Dictionary<string, string[]>> GetDnsBootstrapAsync(CancellationToken ct)
    {
        if (_dnsBootstrapByTld != null && DateTimeOffset.UtcNow - _dnsBootstrapFetchedAt < DnsBootstrapTtl)
            return _dnsBootstrapByTld;

        await DnsBootstrapLock.WaitAsync(ct);
        try
        {
            if (_dnsBootstrapByTld != null && DateTimeOffset.UtcNow - _dnsBootstrapFetchedAt < DnsBootstrapTtl)
                return _dnsBootstrapByTld;

            using var resp = await _httpClient.GetAsync(IanaDnsBootstrapUri, ct);
            resp.EnsureSuccessStatusCode();

            var json = await resp.Content.ReadAsStringAsync(ct);
            var parsed = ParseIanaDnsBootstrap(json);

            _dnsBootstrapByTld = parsed;
            _dnsBootstrapFetchedAt = DateTimeOffset.UtcNow;

            _logger.LogInformation("Fetched and cached IANA RDAP DNS bootstrap. Entries={Count}", parsed.Count);

            return parsed;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch/parse IANA RDAP DNS bootstrap; will use fallback.");

            // If we have a previous cached copy (even if stale), keep using it.
            return _dnsBootstrapByTld ?? new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
        }
        finally
        {
            DnsBootstrapLock.Release();
        }
    }

    private static Dictionary<string, string[]> ParseIanaDnsBootstrap(string json)
    {
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("services", out var services) ||
            services.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException("Invalid IANA RDAP DNS bootstrap JSON: missing 'services'.");
        }

        var map = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);

        foreach (var service in services.EnumerateArray())
        {
            if (service.ValueKind != JsonValueKind.Array || service.GetArrayLength() < 2)
                continue;

            var tlds = service[0];
            var urls = service[1];

            if (tlds.ValueKind != JsonValueKind.Array || urls.ValueKind != JsonValueKind.Array)
                continue;

            var urlList = urls.EnumerateArray()
                .Where(e => e.ValueKind == JsonValueKind.String)
                .Select(e => e.GetString()!)
                .Where(u => !string.IsNullOrWhiteSpace(u))
                .ToArray();

            if (urlList.Length == 0)
                continue;

            foreach (var tldEl in tlds.EnumerateArray())
            {
                if (tldEl.ValueKind != JsonValueKind.String)
                    continue;

                var tld = tldEl.GetString();
                if (string.IsNullOrWhiteSpace(tld))
                    continue;

                // First entry wins
                map.TryAdd(tld, urlList);
            }
        }

        return map;
    }

    // ------------------------
    // Simplified RDAP response model
    // ------------------------

    private sealed class RDAPResponse
    {
        [JsonPropertyName("objectClassName")]
        public string? ObjectClassName { get; set; }

        [JsonPropertyName("handle")]
        public string? Handle { get; set; }

        // Common for domains
        [JsonPropertyName("ldhName")]
        public string? LdhName { get; set; }

        [JsonPropertyName("unicodeName")]
        public string? UnicodeName { get; set; }

        // Common for networks
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("startAddress")]
        public string? StartAddress { get; set; }

        [JsonPropertyName("endAddress")]
        public string? EndAddress { get; set; }

        [JsonPropertyName("ipVersion")]
        public string? IpVersion { get; set; }

        [JsonPropertyName("status")]
        public string[]? Status { get; set; }

        [JsonPropertyName("entities")]
        public RDAPEntity[]? Entities { get; set; }

        [JsonPropertyName("events")]
        public RDAPEvent[]? Events { get; set; }

        [JsonPropertyName("links")]
        public RDAPLink[]? Links { get; set; }

        // Keeps unmodeled fields
        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class RDAPEntity
    {
        [JsonPropertyName("handle")]
        public string? Handle { get; set; }

        [JsonPropertyName("roles")]
        public string[]? Roles { get; set; }

        // IMPORTANT: vcardArray is an array, not an object. Use JsonElement to avoid failures.
        [JsonPropertyName("vcardArray")]
        public JsonElement VCardArray { get; set; }

        [JsonPropertyName("entities")]
        public RDAPEntity[]? Entities { get; set; }

        [JsonPropertyName("events")]
        public RDAPEvent[]? Events { get; set; }

        [JsonPropertyName("links")]
        public RDAPLink[]? Links { get; set; }

        [JsonExtensionData]
        public Dictionary<string, JsonElement>? ExtensionData { get; set; }
    }

    private sealed class RDAPEvent
    {
        [JsonPropertyName("eventAction")]
        public string? EventAction { get; set; }

        [JsonPropertyName("eventDate")]
        public string? EventDate { get; set; }
    }

    private sealed class RDAPLink
    {
        [JsonPropertyName("value")]
        public string? Value { get; set; }

        [JsonPropertyName("rel")]
        public string? Rel { get; set; }

        [JsonPropertyName("type")]
        public string? Type { get; set; }

        [JsonPropertyName("href")]
        public string? Href { get; set; }
    }
}
