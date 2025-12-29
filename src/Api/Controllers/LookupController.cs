using System.ComponentModel.DataAnnotations;
using DistributedLookup.Api.Validation;
using DistributedLookup.Application.UseCases;
using DistributedLookup.Domain.Entities;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;

namespace DistributedLookup.Api.Controllers;

[ApiController]
[Route("api/[controller]")]
public class LookupController : ControllerBase
{
    private readonly SubmitLookupJob _submitJob;
    private readonly GetJobStatus _getStatus;
    private readonly ILogger<LookupController> _logger;

    public LookupController(
        SubmitLookupJob submitJob,
        GetJobStatus getStatus,
        ILogger<LookupController> logger)
    {
        _submitJob = submitJob;
        _getStatus = getStatus;
        _logger = logger;
    }

    /// <summary>
    /// Submit a new lookup job
    /// </summary>
    /// <param name="request">Lookup request with target and optional services</param>
    /// <returns>Job ID for status polling</returns>
    [HttpPost]
    [EnableRateLimiting("expensive")]  // Stricter limit for expensive operations
    [ProducesResponseType(typeof(SubmitResponse), StatusCodes.Status202Accepted)]
    [ProducesResponseType(typeof(ErrorResponse), StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> Submit([FromBody] SubmitRequest request)
    {
        _logger.LogInformation("Received lookup request for target: {Target}", request.Target);

        var result = await _submitJob.ExecuteAsync(
            new SubmitLookupJob.Request(request.Target, request.Services));

        if (!result.IsSuccess)
        {
            return BadRequest(new ErrorResponse { Error = result.Error! });
        }

        var response = new SubmitResponse
        {
            JobId = result.JobId!,
            StatusUrl = Url.Action(nameof(GetStatus), new { jobId = result.JobId })!,
            Message = "Job submitted successfully. Poll the status URL to check progress."
        };

        return AcceptedAtAction(nameof(GetStatus), new { jobId = result.JobId }, response);
    }

    /// <summary>
    /// Get status and results of a lookup job
    /// </summary>
    /// <param name="jobId">Job ID returned from submit</param>
    /// <returns>Current job status and any completed results</returns>
    [HttpGet("{jobId}")]
    [EnableRateLimiting("api-limit")]  // Standard limit for status checks
    [ProducesResponseType(typeof(GetJobStatus.Response), StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status429TooManyRequests)]
    public async Task<IActionResult> GetStatus(string jobId)
    {
        _logger.LogInformation("Checking status for job: {JobId}", jobId);

        var status = await _getStatus.ExecuteAsync(jobId);

        if (status == null)
        {
            return NotFound(new ErrorResponse { Error = "Job not found" });
        }

        return Ok(status);
    }

    /// <summary>
    /// Get available service types
    /// </summary>
    [HttpGet("services")]
    [DisableRateLimiting]  // No rate limit for lightweight metadata
    [ProducesResponseType(typeof(ServiceInfo[]), StatusCodes.Status200OK)]
    public IActionResult GetAvailableServices()
    {
        var services = Enum.GetValues<ServiceType>()
            .Select(s => new ServiceInfo
            {
                Name = s.ToString(),
                Value = s,
                Description = GetServiceDescription(s)
            })
            .ToArray();

        return Ok(services);
    }

    private static string GetServiceDescription(ServiceType service) => service switch
    {
        ServiceType.GeoIP => "Geographic location and ISP information",
        ServiceType.Ping => "Network reachability and latency check",
        ServiceType.RDAP => "Registration data via RDAP protocol",
        ServiceType.ReverseDNS => "Reverse DNS lookup (PTR record)",
        _ => "Unknown service"
    };

    public record SubmitRequest
    {
        [Required(ErrorMessage = "Target is required")]
        [ValidLookupTarget]
        public string Target { get; init; } = string.Empty;

        [ValidServiceTypes]
        public ServiceType[]? Services { get; init; }
    }

    public record SubmitResponse
    {
        public string JobId { get; init; } = string.Empty;
        public string StatusUrl { get; init; } = string.Empty;
        public string Message { get; init; } = string.Empty;
    }

    public record ErrorResponse
    {
        public string Error { get; init; } = string.Empty;
    }

    public record ServiceInfo
    {
        public string Name { get; init; } = string.Empty;
        public ServiceType Value { get; init; }
        public string Description { get; init; } = string.Empty;
    }
}
