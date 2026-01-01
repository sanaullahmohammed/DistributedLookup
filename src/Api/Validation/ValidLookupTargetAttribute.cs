using System.ComponentModel.DataAnnotations;
using System.Globalization;
using System.Net;
using System.Text.RegularExpressions;

namespace DistributedLookup.Api.Validation;

[AttributeUsage(AttributeTargets.Property | AttributeTargets.Field | AttributeTargets.Parameter)]
public sealed class ValidLookupTargetAttribute : ValidationAttribute
{
    // RFC-ish hostname label: 1-63 chars, alnum + hyphen, not starting/ending with hyphen.
    // Must start with a letter for domain names (not all numeric)
    private static readonly Regex LabelRegex =
        new(@"^[A-Za-z0-9](?:[A-Za-z0-9-]{0,61}[A-Za-z0-9])?$",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

    // Pattern to detect IP-like strings (all numeric with dots)
    private static readonly Regex IpLikePattern =
        new(@"^[\d.]+$", RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// If false, requires at least one dot (e.g. "example.com"); rejects single-label names like "localhost".
    /// </summary>
    public bool AllowSingleLabelHostnames { get; init; } = false;

    protected override ValidationResult? IsValid(object? value, ValidationContext validationContext)
    {
        if (value is not string raw || string.IsNullOrWhiteSpace(raw))
            return new ValidationResult("Target is required");

        var target = raw.Trim();

        // 1) Valid IP address?
        // Handle zone ID for IPv6 link-local addresses (e.g., "fe80::1%eth0")
        var targetForParsing = target;
        var zoneIdIndex = target.IndexOf('%');
        if (zoneIdIndex > 0)
        {
            targetForParsing = target[..zoneIdIndex];
        }
        
        if (IPAddress.TryParse(targetForParsing, out var ip))
        {
            // For IPv4, do additional validation to catch malformed addresses
            if (ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
            {
                // Ensure the parsed IP matches the input (catches "1.1.1.1.1.1" edge cases)
                if (ip.ToString() != targetForParsing && !IsValidIpFormat(targetForParsing))
                {
                    return new ValidationResult(
                        $"Invalid IP address format: '{target}'. IPv4 must have exactly 4 octets (0-255), IPv6 must be valid format.");
                }
            }
            // IPv6 is valid if IPAddress.TryParse succeeded
            return ValidationResult.Success;
        }

        // 2) Check if it looks like an invalid IP (all numeric with dots)
        if (IpLikePattern.IsMatch(target))
        {
            return new ValidationResult(
                $"Invalid IP address format: '{target}'. IPv4 must have exactly 4 octets (0-255 each).");
        }

        // 3) DNS name validation
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

        // Validate each label
        foreach (var label in labels)
        {
            if (label.Length > 63)
                return new ValidationResult($"Domain label '{label}' exceeds 63 characters");
            if (!LabelRegex.IsMatch(label))
                return new ValidationResult($"Invalid domain label: '{label}'. Labels must be alphanumeric and may contain hyphens (not at start/end).");
        }

        // TLD validation: last label should not be all numeric
        var tld = labels[^1];
        if (tld.All(char.IsDigit))
            return new ValidationResult($"Invalid TLD: '{tld}'. Top-level domain cannot be all numeric.");

        return ValidationResult.Success;
    }

    /// <summary>
    /// Validates strict IPv4 format: exactly 4 octets, each 0-255
    /// </summary>
    private static bool IsValidIpFormat(string target)
    {
        var parts = target.Split('.');
        
        // IPv4 must have exactly 4 parts
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
            // Reject leading zeros (e.g., "01" or "001")
            if (part.Length > 1 && part[0] == '0')
                return false;
        }

        return true;
    }
}
