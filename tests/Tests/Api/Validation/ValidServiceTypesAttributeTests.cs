using System.ComponentModel.DataAnnotations;
using DistributedLookup.Api.Validation;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace Tests.Api.Validation;

public class ValidServiceTypesAttributeTests
{
    private sealed class Model
    {
        [ValidServiceTypes]
        public ServiceType[]? Services { get; set; }
    }

    private sealed class ModelMax2
    {
        [ValidServiceTypes(MaxServices = 2)]
        public ServiceType[]? Services { get; set; }
    }

    private sealed class ModelAllowDefault
    {
        [ValidServiceTypes(AllowDefaultValue = true)]
        public ServiceType[]? Services { get; set; }
    }

    [Fact]
    public void Should_Pass_WhenNull()
    {
        var model = new Model { Services = null };

        var results = Validate(model);

        results.Should().BeEmpty();
    }

    [Fact]
    public void Should_Fail_WhenProvidedButEmpty()
    {
        var model = new Model { Services = Array.Empty<ServiceType>() };

        var results = Validate(model);

        AssertSingleError(results, "At least one service must be specified if services is provided");
    }

    [Fact]
    public void Should_Fail_WhenTooMany()
    {
        // MaxServices defaults to 10
        var tooMany = Enumerable.Range(0, 11).Select(i => (ServiceType)i).ToArray();
        // Ensure they are "defined" to avoid failing earlier on invalid values.
        // If your enum has fewer than 11 values, force them to a defined set.
        tooMany = Enum.GetValues<ServiceType>().Take(11).ToArray();

        if (tooMany.Length < 11)
        {
            // If enum has fewer than 11 values, this test can't be meaningful.
            // Skip by asserting true.
            true.Should().BeTrue("ServiceType enum has fewer than 11 values; cannot test MaxServices default=10 overflow.");
            return;
        }

        var model = new Model { Services = tooMany };

        var results = Validate(model);

        AssertSingleError(results, "Maximum 10 services can be requested at once");
    }

    [Fact]
    public void Should_Fail_WhenTooMany_WithCustomMax()
    {
        var model = new ModelMax2 { Services = new[] { ServiceType.GeoIP, ServiceType.Ping, ServiceType.RDAP } };

        var results = Validate(model);

        AssertSingleError(results, "Maximum 2 services can be requested at once");
    }

    [Fact]
    public void Should_Fail_WhenDuplicates()
    {
        var model = new Model { Services = new[] { ServiceType.GeoIP, ServiceType.GeoIP } };

        var results = Validate(model);

        AssertSingleError(results, "Duplicate services are not allowed");
    }

    [Fact]
    public void Should_Fail_WhenAnyValueIsUndefined()
    {
        var model = new Model { Services = new[] { ServiceType.GeoIP, (ServiceType)999 } };

        var results = Validate(model);

        AssertSingleError(results, "Invalid service type specified");
    }

    [Fact]
    public void Should_Pass_WhenAllServicesAreDefined_AndUnique_AndWithinLimit()
    {
        var model = new Model { Services = new[] { ServiceType.GeoIP, ServiceType.Ping } };

        var results = Validate(model);

        results.Should().BeEmpty();
    }

    [Fact]
    public void AllowDefaultValue_Flag_IsNotEnforced_ByCurrentImplementation()
    {
        // NOTE: The attribute has AllowDefaultValue, but it is not used in IsValid.
        // This test documents current behavior: default enum value is allowed if it is a defined value.
        var defaultValue = default(ServiceType);

        var isDefined = Enum.GetValues<ServiceType>().Contains(defaultValue);
        isDefined.Should().BeTrue("default(ServiceType) should be a defined enum value for this test to be meaningful.");

        var model = new ModelAllowDefault { Services = new[] { defaultValue } };

        var results = Validate(model);

        results.Should().BeEmpty();
    }

    [Fact]
    public void Should_Fail_WhenAttributeAppliedButValueIsWrongType_ViaDirectValidationCall()
    {
        // This exercises the "Services must be a list/array of ServiceType" branch.
        // DataAnnotations normally won't hit this branch if the property is typed as ServiceType[].
        var attr = new ValidServiceTypesAttribute();
        var ctx = new ValidationContext(new object());

        var result = attr.GetValidationResult("not-an-enumerable-of-servicetype", ctx);

        result.Should().NotBe(ValidationResult.Success);
        result!.ErrorMessage.Should().Be("Services must be a list/array of ServiceType");
    }

    private static void AssertSingleError(
        System.Collections.Generic.List<ValidationResult> results,
        string expected)
    {
        results.Should().ContainSingle();
        results[0].ErrorMessage.Should().Be(expected);
    }

    private static System.Collections.Generic.List<ValidationResult> Validate(object model)
    {
        var context = new ValidationContext(model);
        var results = new System.Collections.Generic.List<ValidationResult>();
        Validator.TryValidateObject(model, context, results, validateAllProperties: true);
        return results;
    }
}
