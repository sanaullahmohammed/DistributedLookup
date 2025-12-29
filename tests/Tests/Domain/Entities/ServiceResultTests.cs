using System.Text.Json;
using DistributedLookup.Domain.Entities;
using FluentAssertions;
using Xunit;

namespace Tests.Domain.Entities;

public class ServiceResultTests
{
    [Fact]
    public void CreateSuccess_ShouldCreateSuccessfulResult_WithSerializedData()
    {
        // Arrange
        var before = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(123);
        var payload = new { ip = "8.8.8.8", score = 42 };

        // Act
        var result = ServiceResult.CreateSuccess(ServiceType.GeoIP, payload, duration);

        var after = DateTime.UtcNow;

        // Assert
        result.ServiceType.Should().Be(ServiceType.GeoIP);
        result.Success.Should().BeTrue();
        result.ErrorMessage.Should().BeNull();
        result.Duration.Should().Be(duration);

        result.CompletedAt.Should().BeOnOrAfter(before);
        result.CompletedAt.Should().BeOnOrBefore(after);
        result.CompletedAt.Kind.Should().Be(DateTimeKind.Utc);

        result.Data.Should().NotBeNull();
        result.Data!.RootElement.GetProperty("ip").GetString().Should().Be("8.8.8.8");
        result.Data!.RootElement.GetProperty("score").GetInt32().Should().Be(42);
    }

    [Fact]
    public void CreateFailure_ShouldCreateFailedResult_WithErrorMessage_AndNoData()
    {
        // Arrange
        var before = DateTime.UtcNow;
        var duration = TimeSpan.FromMilliseconds(250);
        var error = "Timeout calling service";

        // Act
        var result = ServiceResult.CreateFailure(ServiceType.Ping, error, duration);

        var after = DateTime.UtcNow;

        // Assert
        result.ServiceType.Should().Be(ServiceType.Ping);
        result.Success.Should().BeFalse();
        result.ErrorMessage.Should().Be(error);
        result.Duration.Should().Be(duration);

        result.CompletedAt.Should().BeOnOrAfter(before);
        result.CompletedAt.Should().BeOnOrBefore(after);
        result.CompletedAt.Kind.Should().Be(DateTimeKind.Utc);

        result.Data.Should().BeNull();
    }

    [Fact]
    public void CreateSuccess_DataShouldBeValidJsonDocument()
    {
        // Arrange
        var duration = TimeSpan.FromMilliseconds(1);
        var payload = new { nested = new { a = 1 }, list = new[] { 1, 2, 3 } };

        // Act
        var result = ServiceResult.CreateSuccess(ServiceType.GeoIP, payload, duration);

        // Assert
        var json = result.Data!.RootElement.GetRawText();
        Action act = () => JsonDocument.Parse(json);

        act.Should().NotThrow();

        result.Data!.RootElement.GetProperty("nested").GetProperty("a").GetInt32().Should().Be(1);
        result.Data!.RootElement.GetProperty("list")[0].GetInt32().Should().Be(1);
        result.Data!.RootElement.GetProperty("list")[2].GetInt32().Should().Be(3);
    }

    [Fact]
    public void ServiceResult_RecordEquality_ShouldWork_ForSameValues()
    {
        // Arrange
        var completedAt = new DateTime(2025, 01, 01, 0, 0, 0, DateTimeKind.Utc);
        var duration = TimeSpan.FromMilliseconds(10);

        using var doc = JsonDocument.Parse("""{"x":1}""");

        var a = new ServiceResult
        {
            ServiceType = ServiceType.GeoIP,
            Success = true,
            Data = doc, // IMPORTANT: same instance
            ErrorMessage = null,
            CompletedAt = completedAt,
            Duration = duration
        };

        var b = a with { }; // record copy; keeps the same JsonDocument reference

        // Act / Assert
        a.Should().Be(b);
        (a == b).Should().BeTrue();
        a.GetHashCode().Should().Be(b.GetHashCode());
    }

    [Fact]
    public void CreateFailure_ShouldAllowNullOrEmptyErrorMessage_AndKeepItAsProvided()
    {
        // Arrange
        var duration = TimeSpan.FromMilliseconds(5);

        // Act
        var resultWithNull = ServiceResult.CreateFailure(ServiceType.Ping, null!, duration);
        var resultWithEmpty = ServiceResult.CreateFailure(ServiceType.Ping, "", duration);

        // Assert
        resultWithNull.Success.Should().BeFalse();
        resultWithNull.ErrorMessage.Should().BeNull();

        resultWithEmpty.Success.Should().BeFalse();
        resultWithEmpty.ErrorMessage.Should().Be("");
    }
}
