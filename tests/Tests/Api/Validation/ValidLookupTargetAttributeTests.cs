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

        AssertSingleError(result,
            "Domain name contains an invalid label",
            "Domain name contains invalid international characters");
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

        AssertSingleError(result,
            "Domain name contains an invalid label",
            "Domain name contains invalid international characters");
    }

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
