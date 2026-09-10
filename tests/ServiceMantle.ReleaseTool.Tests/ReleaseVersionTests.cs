using ServiceMantle.ReleaseTool;
using Xunit;

namespace ServiceMantle.ReleaseTool.Tests;

public sealed class ReleaseVersionTests
{
    [Theory]
    [InlineData("v0.1.0", "0.1.0")]
    [InlineData("v0.1.0-alpha.1", "0.1.0-alpha.1")]
    [InlineData("v0.1.0-rc.1", "0.1.0-rc.1")]
    [InlineData("v10.20.30", "10.20.30")]
    public void A_tag_publishes_the_version_it_names(string refName, string expected)
    {
        var resolved = ReleaseVersion.Resolve(refName, tagged: true, untaggedVersion: "0.0.0-edge.1");

        Assert.Equal(expected, resolved.Number);
        Assert.True(resolved.Publish);
    }

    [Theory]
    // Build metadata is dropped by NuGet, so two tags differing only after '+' would land on the
    // same package slot.
    [InlineData("v0.1.0+build.5")]
    // Everything below is a version NuGet would silently rewrite, publishing something other than
    // what the tag says.
    [InlineData("v1.2")]
    [InlineData("v01.0.0")]
    [InlineData("v1.0.0.0")]
    [InlineData("vX.Y.Z")]
    [InlineData("v")]
    // A ref that is not a release tag at all.
    [InlineData("0.1.0")]
    [InlineData("main")]
    [InlineData("")]
    public void An_unusable_tag_is_refused(string refName)
    {
        Assert.Throws<ReleaseToolException>(() =>
            ReleaseVersion.Resolve(refName, tagged: true, untaggedVersion: "0.0.0-edge.1"));
    }

    [Fact]
    public void A_ref_that_is_not_a_tag_produces_a_version_that_is_not_published()
    {
        var resolved = ReleaseVersion.Resolve("main", tagged: false, untaggedVersion: "0.0.0-edge.7.1");

        Assert.Equal("0.0.0-edge.7.1", resolved.Number);
        Assert.False(resolved.Publish);
    }

    [Fact]
    public void An_untagged_version_is_held_to_the_same_rules()
    {
        Assert.Throws<ReleaseToolException>(() =>
            ReleaseVersion.Resolve("main", tagged: false, untaggedVersion: "0.0.0+edge.7"));
    }
}
