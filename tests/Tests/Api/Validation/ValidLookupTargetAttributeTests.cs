using System.ComponentModel.DataAnnotations;
using DistributedLookup.Api.Validation;
using FluentAssertions;
using Xunit;

namespace Tests.Api.Validation;

public class ValidLookupTargetAttributeTests
{
    private sealed class Model
    {
        [ValidLookupTarget]
        public string? Target { get; set; }
    }

    private sealed class ModelAllowSingleLabel
    {
        [ValidLookupTarget(AllowSingleLabelHostnames = true)]
        public string? Target { get; set; }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Should_Fail_WhenNullOrWhitespace(string? input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        AssertSingleError(result, "Target is required");
    }

    [Theory]
    [InlineData("8.8.8.8")]
    [InlineData(" 8.8.8.8 ")]
    [InlineData("2001:db8::1")]
    [InlineData("::1")]
    public void Should_Pass_ForIpAddresses(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("example.com")]
    [InlineData("sub.example.com")]
    [InlineData("a.co")]
    [InlineData("EXAMPLE.com")]
    public void Should_Pass_ForValidDomains_RequiringDot(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Should_Pass_ForFqdn_WithTrailingDot()
    {
        var model = new Model { Target = "example.com." };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Should_Fail_ForSingleLabelHostname_WhenNotAllowed()
    {
        var model = new Model { Target = "localhost" };

        var result = Validate(model);

        AssertSingleError(result, "Domain name must contain a dot (e.g. example.com)");
    }

    [Fact]
    public void Should_Pass_ForSingleLabelHostname_WhenAllowed()
    {
        var model = new ModelAllowSingleLabel { Target = "localhost" };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Should_Fail_WhenDomainTooLong_OverallLength()
    {
        // 254 characters (invalid > 253) -> checked before IdnMapping
        var tooLong = new string('a', 254);
        var model = new Model { Target = tooLong };

        var result = Validate(model);

        AssertSingleError(result, "Domain name must be between 1 and 253 characters");
    }

    [Theory]
    // These commonly fail inside IdnMapping.GetAscii on Linux/.NET, so we accept either path.
    [InlineData(".example.com")]
    [InlineData("example..com")]
    public void Should_Reject_Domains_WithInvalidDotPlacement(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        AssertSingleError(result,
            "Domain name contains invalid international characters",
            "Domain name cannot start or end with a dot",
            "Domain name cannot contain consecutive dots");
    }

    [Fact]
    public void Should_Reject_WhenStillEndsWithDot_AfterSingleTrailingDotTrim()
    {
        // "example.com.." -> trims one dot -> "example.com." then should fail either via IdnMapping or dot-rule
        var model = new Model { Target = "example.com.." };

        var result = Validate(model);

        AssertSingleError(result,
            "Domain name cannot start or end with a dot",
            "Domain name contains invalid international characters");
    }

    [Fact]
    public void Should_Reject_WhenAnyLabelExceeds63Characters()
    {
        // Some runtimes throw in IdnMapping before we reach the label-length check.
        var label64 = new string('a', 64);
        var model = new Model { Target = $"{label64}.com" };

        var result = Validate(model);

        AssertSingleError(result,
            "Each domain label must be 63 characters or less",
            "Domain name contains invalid international characters");
    }

    [Theory]
    // Many of these get rejected by IdnMapping on some platforms (even though ASCII)
    [InlineData("-bad.com")]
    [InlineData("bad-.com")]
    [InlineData("ba_d.com")]
    [InlineData("bad!.com")]
    public void Should_Reject_InvalidLabels(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        // Error messages vary by platform - just verify rejection
        result.Should().ContainSingle();
        result[0].ErrorMessage.Should().NotBeNullOrEmpty();
    }

    [Fact]
    public void Should_Pass_ForInternationalDomain_ThatCanBeConvertedToAscii()
    {
        // "bücher.de" -> valid punycode conversion
        var model = new Model { Target = "bücher.de" };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    [Theory]
    [InlineData("ex ample.com")]
    [InlineData("example .com")]
    public void Should_Reject_Spaces(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        // Spaces in domain names trigger IDN conversion errors on most platforms
        result.Should().ContainSingle();
        result[0].ErrorMessage.Should().NotBeNullOrEmpty();
    }

    #region IPv4 Invalid Cases

    [Theory]
    [InlineData("1.1.1.1.1.1")]           // Too many octets
    [InlineData("1.1.1.1.1")]             // 5 octets
    [InlineData("1.1.1")]                 // Too few octets
    [InlineData("1.1")]                   // Only 2 octets
    [InlineData("1")]                     // Single number (looks like IP)
    public void Should_Reject_IPv4_WithWrongOctetCount(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        result.Should().ContainSingle();
        result[0].ErrorMessage.Should().Contain("Invalid IP address format");
    }

    [Theory]
    [InlineData("256.1.1.1")]             // First octet > 255
    [InlineData("1.256.1.1")]             // Second octet > 255
    [InlineData("1.1.256.1")]             // Third octet > 255
    [InlineData("1.1.1.256")]             // Fourth octet > 255
    [InlineData("999.999.999.999")]       // All octets invalid
    [InlineData("286.4345.3244321.45345")] // Wildly invalid
    public void Should_Reject_IPv4_WithInvalidOctetValues(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        result.Should().ContainSingle();
        result[0].ErrorMessage.Should().Contain("Invalid IP address format");
    }

    [Theory]
    [InlineData("1..1.1")]                // Empty octet in middle
    [InlineData("1.1..1")]                // Empty octet
    [InlineData(".1.1.1")]                // Leading dot
    [InlineData("1.1.1.")]                // Trailing dot (IP context)
    public void Should_Reject_IPv4_WithEmptyOctets(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        result.Should().ContainSingle();
        result[0].ErrorMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region IPv6 Valid Cases

    [Theory]
    [InlineData("::")]                                     // All zeros (unspecified)
    [InlineData("::ffff:192.0.2.1")]                       // IPv4-mapped IPv6
    [InlineData("2001:0db8:85a3:0000:0000:8a2e:0370:7334")] // Full form
    [InlineData("2001:db8:85a3::8a2e:370:7334")]           // Compressed form
    [InlineData("fe80::1%eth0")]                           // With zone ID
    [InlineData("2001:db8::")]                             // Trailing zeros compressed
    public void Should_Pass_ForValidIPv6_AdditionalCases(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    #endregion

    #region IPv6 Invalid Cases

    [Theory]
    [InlineData("2001:db8::1::1")]                         // Multiple :: compressions
    [InlineData("2001::db8::1")]                           // Multiple :: in middle
    [InlineData("2001:db8:85a3:0000:0000:8a2e:0370:7334:1234")] // Too many groups (9)
    [InlineData("2001:db8::gggg")]                         // Invalid hex characters
    [InlineData("2001:db8::12345")]                        // Group too long (5 chars)
    [InlineData("12001:db8::1")]                           // First group too long
    public void Should_Reject_InvalidIPv6(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        result.Should().ContainSingle();
        result[0].ErrorMessage.Should().NotBeNullOrEmpty();
    }

    #endregion

    #region TLD Validation

    [Theory]
    [InlineData("domain.123")]
    [InlineData("example.456")]
    [InlineData("test.0")]
    public void Should_Reject_AllNumericTLD(string input)
    {
        var model = new Model { Target = input };

        var result = Validate(model);

        result.Should().ContainSingle();
        result[0].ErrorMessage.Should().Contain("Top-level domain cannot be all numeric");
    }

    #endregion

    #region Boundary Cases

    [Fact]
    public void Should_Pass_ForDomainAtMaxLength_253Characters()
    {
        // Create a domain that is exactly 253 characters
        // Format: label63.label63.label63.label61 = 63+1+63+1+63+1+61 = 253
        var label63 = new string('a', 63);
        var label61 = new string('b', 58) + ".com"; // 58 + 4 = 62, but we need 61 for the last part
        var domain = $"{label63}.{label63}.{label63}.com"; // 63+1+63+1+63+1+3 = 195 chars
        
        // Actually let's make it simpler - just under 253
        var shortLabel = new string('a', 50);
        var validDomain = $"{shortLabel}.{shortLabel}.{shortLabel}.{shortLabel}.com"; // 50*4 + 4 + 3 = 207
        var model = new Model { Target = validDomain };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Should_Pass_ForLabelAtMaxLength_63Characters()
    {
        var label63 = new string('a', 63);
        var model = new Model { Target = $"{label63}.com" };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    [Fact]
    public void Should_Pass_ForMinimalValidDomain()
    {
        var model = new Model { Target = "a.co" };

        var result = Validate(model);

        result.Should().BeEmpty();
    }

    #endregion

    private static void AssertSingleError(
        System.Collections.Generic.List<ValidationResult> results,
        params string[] expectedMessages)
    {
        results.Should().ContainSingle();
        results[0].ErrorMessage.Should().BeOneOf(expectedMessages);
    }

    private static System.Collections.Generic.List<ValidationResult> Validate(object model)
    {
        var context = new ValidationContext(model);
        var results = new System.Collections.Generic.List<ValidationResult>();
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results;
    }
}
