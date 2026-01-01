namespace DistributedLookup.Domain.Entities;

/// <summary>
/// Factory for creating LookupJob instances with consistent timestamps.
/// Ensures CreatedAt is always set to the same value used in the JobSubmitted event.
/// </summary>
public static class LookupJobFactory
{
    /// <summary>
    /// Creates a new LookupJob with a specific creation timestamp.
    /// Use this to ensure the job and saga have consistent CreatedAt values.
    /// </summary>
    /// <param name="jobId">Unique job identifier</param>
    /// <param name="target">The lookup target (IP or domain)</param>
    /// <param name="targetType">Type of the target</param>
    /// <param name="services">Services to run for this job</param>
    /// <param name="createdAt">The creation timestamp (should match JobSubmitted.Timestamp)</param>
    /// <returns>A new LookupJob instance</returns>
    public static LookupJob Create(
        string jobId,
        string target,
        LookupTarget targetType,
        IEnumerable<ServiceType> services,
        DateTime createdAt)
    {
        return new LookupJob(jobId, target, targetType, services, createdAt);
    }
}
