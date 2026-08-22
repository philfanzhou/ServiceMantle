using ServiceMantle;
using Xunit;

namespace ServiceMantle.Tests;

public sealed class InstanceIdTests
{
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
