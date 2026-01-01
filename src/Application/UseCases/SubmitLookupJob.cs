using DistributedLookup.Application.Interfaces;
using DistributedLookup.Contracts.Events;
using DistributedLookup.Domain.Entities;
using MassTransit;

namespace DistributedLookup.Application.UseCases;

/// <summary>
/// Use case for submitting a new lookup job.
/// Follows CQRS pattern - this is a command handler.
/// </summary>
public class SubmitLookupJob
{
    private readonly IJobRepository _repository;
    private readonly IPublishEndpoint _publisher;

    public SubmitLookupJob(IJobRepository repository, IPublishEndpoint publisher)
    {
        _repository = repository;
        _publisher = publisher;
    }

    public async Task<Result> ExecuteAsync(Request request, CancellationToken cancellationToken = default)
    {
        // Validate input
        var validationResult = ValidateRequest(request);
        if (!validationResult.IsValid)
        {
            return Result.Failure(validationResult.Error!);
        }

        // Create job entity using factory pattern with shared timestamp
        // This ensures CreatedAt is consistent between job, saga, and event
        var jobId = Guid.NewGuid().ToString();
        var createdAt = DateTime.UtcNow;
        var services = request.Services?.Any() == true 
            ? request.Services 
            : GetDefaultServices();

        var job = LookupJobFactory.Create(
            jobId,
            request.Target,
            validationResult.TargetType!.Value,
            services,
            createdAt
        );

        // Persist job
        await _repository.SaveAsync(job, cancellationToken);

        // Publish event with same timestamp as job creation
        await _publisher.Publish(new JobSubmitted
        {
            JobId = jobId,
            Target = request.Target,
            TargetType = validationResult.TargetType.Value,
            Services = services.ToArray(),
            Timestamp = createdAt
        }, cancellationToken);

        return Result.Success(jobId);
    }

    private ValidationResult ValidateRequest(Request request)
    {
        if (string.IsNullOrWhiteSpace(request.Target))
        {
            return ValidationResult.Invalid("Target cannot be empty");
        }

        var target = request.Target.Trim();

        // Check for valid IPv4 address (strict validation)
        if (IsValidIPv4(target))
        {
            return ValidationResult.Valid(LookupTarget.IPAddress);
        }

        // Check for valid IPv6 address
        if (System.Net.IPAddress.TryParse(target, out var ip) && ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetworkV6)
        {
            return ValidationResult.Valid(LookupTarget.IPAddress);
        }

        // Check if it looks like an invalid IP (all numeric with dots)
        if (System.Text.RegularExpressions.Regex.IsMatch(target, @"^[\d.]+$"))
        {
            return ValidationResult.Invalid($"Invalid IP address format: '{target}'. IPv4 must have exactly 4 octets (0-255 each).");
        }

        // Domain validation
        if (IsValidDomain(target))
        {
            return ValidationResult.Valid(LookupTarget.Domain);
        }

        return ValidationResult.Invalid("Target must be a valid IP address or domain name");
    }

    private static bool IsValidIPv4(string target)
    {
        var parts = target.Split('.');
        if (parts.Length != 4)
            return false;

        foreach (var part in parts)
        {
            if (string.IsNullOrEmpty(part))
                return false;
            if (!int.TryParse(part, out var octet))
                return false;
            if (octet < 0 || octet > 255)
                return false;
            // Reject leading zeros
            if (part.Length > 1 && part[0] == '0')
                return false;
        }

        return true;
    }

    private static bool IsValidDomain(string target)
    {
        // Strip trailing dot for FQDN
        if (target.EndsWith('.'))
            target = target[..^1];

        if (target.Length < 1 || target.Length > 253)
            return false;

        // Must contain at least one dot
        if (!target.Contains('.'))
            return false;

        // Cannot have consecutive dots
        if (target.Contains(".."))
            return false;

        var labels = target.Split('.');
        
        // TLD cannot be all numeric
        var tld = labels[^1];
        if (tld.All(char.IsDigit))
            return false;

        // Validate each label
        foreach (var label in labels)
        {
            if (label.Length < 1 || label.Length > 63)
                return false;
            // Must start and end with alphanumeric
            if (!char.IsLetterOrDigit(label[0]) || !char.IsLetterOrDigit(label[^1]))
                return false;
            // Can only contain alphanumeric and hyphens
            if (!label.All(c => char.IsLetterOrDigit(c) || c == '-'))
                return false;
        }

        return true;
    }

    private static IEnumerable<ServiceType> GetDefaultServices()
    {
        return new[] { ServiceType.GeoIP, ServiceType.Ping, ServiceType.RDAP, ServiceType.ReverseDNS };
    }

    public record Request(string Target, IEnumerable<ServiceType>? Services = null);

    public record Result
    {
        public bool IsSuccess { get; init; }
        public string? JobId { get; init; }
        public string? Error { get; init; }

        public static Result Success(string jobId) => new() { IsSuccess = true, JobId = jobId };
        public static Result Failure(string error) => new() { IsSuccess = false, Error = error };
    }

    private record ValidationResult
    {
        public bool IsValid { get; init; }
        public LookupTarget? TargetType { get; init; }
        public string? Error { get; init; }

        public static ValidationResult Valid(LookupTarget targetType) => 
            new() { IsValid = true, TargetType = targetType };
        
        public static ValidationResult Invalid(string error) => 
            new() { IsValid = false, Error = error };
    }
}
