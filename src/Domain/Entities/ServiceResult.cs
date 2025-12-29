using System.Text.Json;

namespace DistributedLookup.Domain.Entities;

/// <summary>
/// Value object representing the result of a service lookup.
/// Immutable by design.
/// </summary>
public record ServiceResult
{
    public ServiceType ServiceType { get; init; }
    public bool Success { get; init; }
    public JsonDocument? Data { get; init; }
    public string? ErrorMessage { get; init; }
    public DateTime CompletedAt { get; init; }
    public TimeSpan Duration { get; init; }

    public static ServiceResult CreateSuccess(ServiceType serviceType, object data, TimeSpan duration)
    {
        return new ServiceResult
        {
            ServiceType = serviceType,
            Success = true,
            Data = JsonSerializer.SerializeToDocument(data),
            CompletedAt = DateTime.UtcNow,
            Duration = duration
        };
    }

    public static ServiceResult CreateFailure(ServiceType serviceType, string errorMessage, TimeSpan duration)
    {
        return new ServiceResult
        {
            ServiceType = serviceType,
            Success = false,
            ErrorMessage = errorMessage,
            CompletedAt = DateTime.UtcNow,
            Duration = duration
        };
    }
}
