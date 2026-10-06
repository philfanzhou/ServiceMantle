using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

public sealed class ReferenceSetupCodeBannerTests
{
    private const string Code = "0123456789abcdefABCDEFGHIJKLMNOP";

    [Theory]
    [InlineData("")]
    [InlineData("info: concurrent startup log\n    a message containing \"quotes\"\n")]
    [InlineData("stderr: unrelated startup warning\n")]
    public void A_complete_banner_accepts_only_the_code_despite_interleaved_logs(string noise)
    {
        var output = "one-time setup code:\n" + noise + Code +
            "\ninfo: another log\nexpires at 2026-10-06T00:00:00Z\n";
        Assert.True(ReferenceSetupCodeBanner.TryRead(output, out var actual));
        Assert.Equal(Code, actual);
    }

    [Theory]
    [InlineData("one-time setup code:\n")]
    [InlineData("one-time setup code:\n" + Code + "\n")]
    [InlineData("one-time setup code:\nnot-a-code\nexpires at now\n")]
    [InlineData("one-time setup code:\n " + Code + "\nexpires at now\n")]
    [InlineData("one-time setup code:\n" + Code + "\n" + Code + "\nexpires at now\n")]
    [InlineData("one-time setup code:\n" + Code + "\nexpires at now\none-time setup code:\n")]
    public void Partial_malformed_or_ambiguous_output_is_not_used_as_a_request_code(string output)
    {
        Assert.False(ReferenceSetupCodeBanner.TryRead(output, out var actual));
        Assert.Empty(actual);
    }
}
