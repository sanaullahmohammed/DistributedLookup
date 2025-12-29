namespace DistributedLookup.Domain.Entities;

public enum JobStatus
{
    Pending,
    Processing,
    Completed,
    Failed
}

public enum ServiceType
{
    GeoIP,
    Ping,
    RDAP,
    ReverseDNS
}

public enum LookupTarget
{
    IPAddress,
    Domain
}
