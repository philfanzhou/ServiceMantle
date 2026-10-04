using ServiceMantle.Diagnostics;
using Xunit;

namespace ServiceMantle.Tests.Diagnostics;

public sealed class FixedRemoteTelemetryAuthenticationResolverTests
{
    [Theory]
    [InlineData("other")]
    [InlineData("PRIVATE-name")]
    [InlineData("")]
    [InlineData(null)]
    public void Exact_ordinal_lookup_and_failed_out_value_never_reuse_previous_secret(string? other)
    {
        var resolver = new FixedRemoteTelemetryAuthenticationResolver("private-name", "Authorization", "fixed-secret");
        Assert.True(resolver.TryResolve("private-name", out var header));
        Assert.Equal("Authorization", header!.Name); Assert.Equal("fixed-secret", header.Value);
        Assert.False(resolver.TryResolve(other!, out header)); Assert.Null(header);
        Assert.DoesNotContain("fixed-secret", resolver.ToString());
        Assert.DoesNotContain("private-name", resolver.ToString());
        Assert.Equal(typeof(ServiceId).Assembly, resolver.GetType().Assembly);
    }

    public static TheoryData<string?, string?, string?> InvalidInputs => new()
    {
        { null, "Authorization", "secret" }, { "", "Authorization", "secret" },
        { " name", "Authorization", "secret" }, { "na雪me", "Authorization", "secret" },
        { new string('n', 129), "Authorization", "secret" }, { "private-name", null, "secret" },
        { "private-name", "", "secret" }, { "private-name", "bad name", "secret" },
        { "private-name", "bad:token", "secret" }, { "private-name", "bad\nname", "secret" },
        { "private-name", "雪", "secret" }, { "private-name", new string('h', 129), "secret" },
        { "private-name", "Authorization", null }, { "private-name", "Authorization", "" },
        { "private-name", "Authorization", "secret\rvalue" }, { "private-name", "Authorization", "secret\nvalue" }
    };

    [Theory]
    [MemberData(nameof(InvalidInputs))]
    public void Invalid_constructor_inputs_have_fixed_messages_and_no_submitted_material_or_inner(string? name, string? header, string? value)
    {
        var error = Assert.ThrowsAny<ArgumentException>(() => new FixedRemoteTelemetryAuthenticationResolver(name!, header!, value!));
        Assert.Null(error.InnerException);
        Assert.DoesNotContain("private-name", error.ToString());
        Assert.DoesNotContain("secret", error.ToString());
        Assert.DoesNotContain("雪", error.ToString());
    }

    [Theory]
    [InlineData("雪 control\0\u0001 value")]
    [InlineData("  spaced untrimmed  ")]
    public async Task Accepted_value_domain_and_length_boundaries_match_existing_OTLP_validation(string value)
    {
        var resolver = new FixedRemoteTelemetryAuthenticationResolver(new string('n', 128), new string('h', 128), value);
        Assert.True(resolver.TryResolve(new string('n', 128), out var first));
        var headers = await Task.WhenAll(Enumerable.Range(0, 128).Select(_ => Task.Run(() =>
        { Assert.True(resolver.TryResolve(new string('n', 128), out var header)); return header; }, TestContext.Current.CancellationToken)));
        Assert.All(headers, header => { Assert.Same(first, header); Assert.Equal(value, header!.Value); });
        Assert.True(new FixedRemoteTelemetryAuthenticationResolver("-", "!#$%&'*+-.^_`|~", "x").TryResolve("-", out _));
    }
}
