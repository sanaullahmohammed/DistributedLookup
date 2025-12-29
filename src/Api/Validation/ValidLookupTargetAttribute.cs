using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace DistributedLookup.Api.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ValidLookupTargetAttribute : ValidationAttribute
{
    // RFC-ish hostname label: 1-63 chars, alnum + hyphen, not starting/ending with hyphen.
    private static readonly Regex LabelRegex =
        new(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// If false, requires at least one dot (e.g. "example.com"); rejects single-label names like "localhost".
    /// </summary>
    public bool AllowSingleLabelHostnames { get; init; } = false;

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string raw || string.IsNullOrWhiteSpace(raw))
            return new ValidationResult("Target is required");

        var target = raw.Trim();

        // 1) IP literal?
        if (IPAddress.TryParse(target, out _))
            return ValidationResult.Success;

        // 2) DNS name (normalize)
        // Allow a trailing dot for FQDN, but strip it for validation.
        if (target.EndsWith(".", StringComparison.Ordinal))
            target = target[..^1];

        if (target.Length is < 1 or > 253)
            return new ValidationResult("Domain name must be between 1 and 253 characters");

        // Convert Unicode IDN -> ASCII (punycode). If you want to *reject* IDN, remove this.
        string asciiHost;
        try
        {
            asciiHost = new IdnMapping().GetAscii(target);
        }
        catch
        {
            return new ValidationResult("Domain name contains invalid international characters");
        }

        // Basic dot rules
        if (asciiHost.StartsWith(".", StringComparison.Ordinal) || asciiHost.EndsWith(".", StringComparison.Ordinal))
            return new ValidationResult("Domain name cannot start or end with a dot");
        if (asciiHost.Contains("..", StringComparison.Ordinal))
            return new ValidationResult("Domain name cannot contain consecutive dots");

        var labels = asciiHost.Split('.', StringSplitOptions.RemoveEmptyEntries);

        if (!AllowSingleLabelHostnames && labels.Length < 2)
            return new ValidationResult("Domain name must contain a dot (e.g. example.com)");

        foreach (var label in labels)
        {
            if (label.Length > 63)
                return new ValidationResult("Each domain label must be 63 characters or less");
            if (!LabelRegex.IsMatch(label))
                return new ValidationResult("Domain name contains an invalid label");
        }

        return ValidationResult.Success;
    }
}
