using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Contracts.Commands;

/// <summary>
/// Base interface for lookup commands
/// </summary>
public interface ILookupCommand
{
    string JobId { get; }
    string Target { get; }
    LookupTarget TargetType { get; }
}

/// <summary>
/// Command to perform GeoIP lookup
/// </summary>
public record CheckGeoIP : ILookupCommand
{
    public string JobId { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public LookupTarget TargetType { get; init; }
}

/// <summary>
/// Command to perform Ping check
/// </summary>
public record CheckPing : ILookupCommand
{
    public string JobId { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public LookupTarget TargetType { get; init; }
}

/// <summary>
/// Command to perform RDAP lookup
/// </summary>
public record CheckRDAP : ILookupCommand
{
    public string JobId { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public LookupTarget TargetType { get; init; }
}

/// <summary>
/// Command to perform Reverse DNS lookup
/// </summary>
public record CheckReverseDNS : ILookupCommand
{
    public string JobId { get; init; } = string.Empty;
    public string Target { get; init; } = string.Empty;
    public LookupTarget TargetType { get; init; }
}
