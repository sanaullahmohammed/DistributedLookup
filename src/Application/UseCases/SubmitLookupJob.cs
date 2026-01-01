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

        // Determine if it's an IP or domain
        if (System.Net.IPAddress.TryParse(request.Target, out _))
        {
            return ValidationResult.Valid(LookupTarget.IPAddress);
        }

        // Basic domain validation - in production, use more robust validation
        if (request.Target.Contains('.') && !request.Target.Contains(' '))
        {
            return ValidationResult.Valid(LookupTarget.Domain);
        }

        return ValidationResult.Invalid("Target must be a valid IP address or domain");
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
