using ServiceMantle;
using Xunit;

namespace ServiceMantle.Tests;

public sealed class ServiceIdTests
{
    [Fact]
    public void Parse_trims_and_normalizes_to_lowercase()
    {
        var serviceId = ServiceId.Parse("  SignaCore-Prod  ");

        Assert.Equal("signacore-prod", serviceId.Value);
        Assert.Equal("signacore-prod", serviceId.ToString());
    }

    [Fact]
    public void TryParse_successfully_trims_and_normalizes_to_lowercase()
    {
        var parsed = ServiceId.TryParse("  SignaCore-Prod  ", out var serviceId);

        Assert.True(parsed);
        Assert.NotNull(serviceId);
        Assert.Equal("signacore-prod", serviceId!.Value);
    }

    [Fact]
    public void Equivalent_normalized_values_are_equal()
    {
        var first = ServiceId.Parse("Service_01");
        var second = ServiceId.Parse(" service_01 ");

        Assert.Equal(first, second);
    }

    [Fact]
    public void Parse_accepts_valid_characters_after_the_first_character()
    {
        var serviceId = ServiceId.Parse("identity.ap-southeast-1");

        Assert.Equal("identity.ap-southeast-1", serviceId.Value);
    }

    [Fact]
    public void TryParse_rejects_invalid_leading_characters()
    {
        foreach (var value in new[] { ".admin", "_admin", "-admin" })
        {
            Assert.False(ServiceId.TryParse(value, out var serviceId));
            Assert.Null(serviceId);
        }
    }

    [Fact]
    public void TryParse_rejects_invalid_characters_and_internal_spaces()
    {
        foreach (var value in new[] { "service/name", "service name", "service@prod" })
        {
            Assert.False(ServiceId.TryParse(value, out var serviceId));
            Assert.Null(serviceId);
        }
    }

    [Fact]
    public void Parse_rejects_null_empty_whitespace_and_overlong_values()
    {
        Assert.Throws<ArgumentNullException>(() => ServiceId.Parse(null!));
        Assert.Throws<FormatException>(() => ServiceId.Parse(string.Empty));
        Assert.Throws<FormatException>(() => ServiceId.Parse("   "));
        Assert.Throws<FormatException>(() => ServiceId.Parse(new string('a', 129)));
    }

    [Fact]
    public void TryParse_returns_false_and_null_for_invalid_input()
    {
        var parsed = ServiceId.TryParse("invalid/value", out var serviceId);

        Assert.False(parsed);
        Assert.Null(serviceId);
    }
}
