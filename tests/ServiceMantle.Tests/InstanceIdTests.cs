using ServiceMantle;
using Xunit;

namespace ServiceMantle.Tests;

public sealed class InstanceIdTests
{
    public static IEnumerable<object[]> ServiceIdSpellings()
    {
        yield return new object[] { "catalog", "catalog" };
        yield return new object[] { "  Catalog  ", "catalog" };
        yield return new object[] { "Doctheca", "doctheca" };
        yield return new object[] { "ruoyu.admin", "ruoyu.admin" };
        yield return new object[]
        {
            new string('a', 128),
            new string('a', 128)
        };
    }

    [Theory]
    [MemberData(nameof(ServiceIdSpellings))]
    public void CreateRandom_round_trips_and_prefixes_the_normalized_service_id(
        string spelling,
        string normalized)
    {
        var generated = InstanceId.CreateRandom(ServiceId.Parse(spelling));

        var roundTrip = InstanceId.Parse(generated.Value);
        Assert.Equal(generated.Value, roundTrip.Value);
        Assert.True(InstanceId.TryParse(generated.Value, out _));
        Assert.StartsWith(normalized + "-", generated.Value, StringComparison.Ordinal);
        Assert.Equal(normalized.Length + 1 + 32, generated.Value.Length);
        var suffix = generated.Value[(normalized.Length + 1)..];
        Assert.Matches("^[0-9a-f]{32}$", suffix);
    }

    [Fact]
    public void CreateRandom_yields_distinct_values_on_repeated_calls()
    {
        var serviceId = ServiceId.Parse("catalog");
        var values = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < 1_000; index++)
        {
            values.Add(InstanceId.CreateRandom(serviceId).Value);
        }

        Assert.Equal(1_000, values.Count);
    }

    [Fact]
    public void CreateRandom_rejects_a_null_service_id()
    {
        Assert.Throws<ArgumentNullException>(() => InstanceId.CreateRandom(null!));
    }

    [Fact]
    public void Parse_trims_but_preserves_case()
    {
        var instanceId = InstanceId.Parse("  Node-A3  ");

        Assert.Equal("Node-A3", instanceId.Value);
        Assert.Equal("Node-A3", instanceId.ToString());
    }

    [Fact]
    public void TryParse_successfully_trims_and_preserves_case()
    {
        var parsed = InstanceId.TryParse("  Node-A3  ", out var instanceId);

        Assert.True(parsed);
        Assert.NotNull(instanceId);
        Assert.Equal("Node-A3", instanceId!.Value);
    }

    [Fact]
    public void Parse_accepts_a_valid_instance_name()
    {
        var instanceId = InstanceId.Parse("pod/service-01@node-2");

        Assert.Equal("pod/service-01@node-2", instanceId.Value);
    }

    [Fact]
    public void Parse_rejects_null_empty_whitespace_overlong_and_control_values()
    {
        Assert.Throws<ArgumentNullException>(() => InstanceId.Parse(null!));
        Assert.Throws<FormatException>(() => InstanceId.Parse(string.Empty));
        Assert.Throws<FormatException>(() => InstanceId.Parse("   "));
        Assert.Throws<FormatException>(() => InstanceId.Parse(new string('a', 257)));
        Assert.Throws<FormatException>(() => InstanceId.Parse("node\n01"));
    }

    [Fact]
    public void TryParse_returns_false_and_null_for_invalid_input()
    {
        var parsed = InstanceId.TryParse("node\001", out var instanceId);

        Assert.False(parsed);
        Assert.Null(instanceId);
    }
}
