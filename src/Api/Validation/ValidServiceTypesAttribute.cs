using System.ComponentModel.DataAnnotations;
using DistributedLookup.Domain.Entities;

namespace DistributedLookup.Api.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ValidServiceTypesAttribute : ValidationAttribute
{
    public int MaxServices { get; init; } = 10;

    /// <summary>
    /// If true, allows "0"/default enum value (often "None" or "Unknown").
    /// Set to false if you want to block it.
    /// </summary>
    public bool AllowDefaultValue { get; init; } = false;

    private static readonly HashSet<ServiceType> DefinedValues =
        Enum.GetValues<ServiceType>().ToHashSet();

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        // Optional field
        if (value is null)
            return ValidationResult.Success;

        if (value is not IEnumerable<ServiceType> enumerable)
            return new ValidationResult("Services must be a list/array of ServiceType");

        var services = enumerable as ServiceType[] ?? enumerable.ToArray();

        if (services.Length == 0)
            return new ValidationResult("At least one service must be specified if services is provided");

        if (services.Length > MaxServices)
            return new ValidationResult($"Maximum {MaxServices} services can be requested at once");

        // Duplicates
        if (services.Length != services.Distinct().Count())
            return new ValidationResult("Duplicate services are not allowed");

        // Validate defined enum values
        if (services.Any(s => !DefinedValues.Contains(s)))
            return new ValidationResult("Invalid service type specified");

        return ValidationResult.Success;
    }
}
