using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class SensitiveValueUnicodeBoundaryTests
{
    private const string RootKey = "root-key-with-enough-entropy-for-tests-4fba32";
    private const string MalformedUtf16Message =
        "The value is not well-formed UTF-16 because it contains unpaired surrogates.";

    // Fixed legal sm:v1 vector generated on pre-fix main (5bed830) from synthetic test
    // material only; the fix must not change salt, derivation context, or envelope version.
    private const string CompatibilityServiceId = "unicode-vector-svc";
    private const string CompatibilityPurpose = "configuration.unicode.compatibility-vector";
    private const string CompatibilityRootKey = "synthetic-root-key-for-compatibility-vector-476";
    private const string CompatibilityPlaintext = "synthetic-value-\u4E2D\u6587-\uD83D\uDE00-\uFFFD-end";
    private const string CompatibilityEnvelope =
        "sm:v1:JajP3DWuQUzZED4CWTKC4UyrC3SH9jkpDP5xwT3f+/VmjiJkRnazU0QxxBsRChmMT5SymqvkLbDIFMnx4aay";

    private static readonly ServiceId TestService = ServiceId.Parse("orders-api");

    public static TheoryData<string> MalformedUtf16Samples => new()
    {
        "\uD800",              // lone high surrogate
        "\uDC00",              // lone low surrogate
        "\uD800A",             // high surrogate followed by a normal character
        "A\uD800",             // lone high surrogate at the end
        "pre\uDC00mid",        // lone low surrogate in the middle
        "\uDC00\uD800",        // surrogates in reversed order
        "\uD83D\uDE00\uD800",  // valid pair followed by a lone high surrogate
        "\uDBFF",              // boundary high surrogate
        "\uDFFF",              // boundary low surrogate
    };

    [Fact]
    public void Unprotect_PreFixCompatibilityVector_StillDecrypts()
    {
        var protector = new SensitiveValueProtector(
            ServiceId.Parse(CompatibilityServiceId),
            CompatibilityPurpose);

        var result = protector.Unprotect(CompatibilityEnvelope, CompatibilityRootKey, TestCancellationToken);

        Assert.Equal(CompatibilityPlaintext, result);
    }

    [Theory]
    [MemberData(nameof(MalformedUtf16Samples))]
    public void Constructor_WithMalformedPurpose_RejectsWithFixedArgumentException(string purpose)
    {
        var exception = Assert.Throws<ArgumentException>(
            () => new SensitiveValueProtector(TestService, purpose));

        Assert.Equal("purpose", exception.ParamName);
        Assert.StartsWith(MalformedUtf16Message, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(purpose, exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedUtf16Samples))]
    public void Protect_WithMalformedPlaintext_RejectsWithFixedArgumentException(string plaintext)
    {
        var protector = CreateProtector();

        var exception = Assert.Throws<ArgumentException>(
            () => protector.Protect(plaintext, RootKey, TestCancellationToken));

        Assert.Equal("plaintext", exception.ParamName);
        Assert.StartsWith(MalformedUtf16Message, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        var diagnostic = exception.ToString();
        Assert.DoesNotContain(plaintext, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedUtf16Samples))]
    public void Protect_WithMalformedRootKey_RejectsWithFixedArgumentException(string rootKey)
    {
        var protector = CreateProtector();

        var exception = Assert.Throws<ArgumentException>(
            () => protector.Protect("secret", rootKey, TestCancellationToken));

        Assert.Equal("rootKey", exception.ParamName);
        Assert.StartsWith(MalformedUtf16Message, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        var diagnostic = exception.ToString();
        Assert.DoesNotContain(rootKey, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostic, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(MalformedUtf16Samples))]
    public void Unprotect_WithMalformedRootKey_RejectsWithFixedArgumentException(string rootKey)
    {
        var protector = CreateProtector();
        var protectedValue = protector.Protect("secret", RootKey, TestCancellationToken);

        var exception = Assert.Throws<ArgumentException>(
            () => protector.Unprotect(protectedValue, rootKey, TestCancellationToken));

        Assert.Equal("rootKey", exception.ParamName);
        Assert.StartsWith(MalformedUtf16Message, exception.Message, StringComparison.Ordinal);
        Assert.Null(exception.InnerException);
        var diagnostic = exception.ToString();
        Assert.DoesNotContain(rootKey, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain("secret", diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedValue, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void FoldedPurposeSamples_CanNoLongerCrossDecrypt()
    {
        var protectedValue = new SensitiveValueProtector(TestService, "p\uFFFD")
            .Protect("secret", RootKey, TestCancellationToken);

        // Pre-fix, both folded purposes encoded to the same bytes as "p\uFFFD" and could
        // decrypt the envelope above across contexts.
        var first = Assert.Throws<ArgumentException>(
            () => new SensitiveValueProtector(TestService, "p\uD800"));
        var second = Assert.Throws<ArgumentException>(
            () => new SensitiveValueProtector(TestService, "p\uD801"));

        Assert.Equal("purpose", first.ParamName);
        Assert.Equal("purpose", second.ParamName);
        Assert.Throws<ArgumentException>(
            () => new SensitiveValueProtector(TestService, "p\uD800")
                .Unprotect(protectedValue, RootKey, TestCancellationToken));
    }

    [Fact]
    public void FoldedRootKeySamples_CanNoLongerCrossDecrypt()
    {
        const string foldedRootKey = "root-key-with-a-real-replacement-char-\uFFFD-476";
        var protector = CreateProtector();
        var protectedValue = protector.Protect("secret", foldedRootKey, TestCancellationToken);

        // Pre-fix, both folded root keys encoded to the same bytes as the U+FFFD key above
        // and decrypted the envelope successfully.
        foreach (var surrogateRootKey in new[] { "root-key-with-a-real-replacement-char-\uD800-476", "root-key-with-a-real-replacement-char-\uD801-476" })
        {
            var protectException = Assert.Throws<ArgumentException>(
                () => protector.Protect("secret", surrogateRootKey, TestCancellationToken));
            var unprotectException = Assert.Throws<ArgumentException>(
                () => protector.Unprotect(protectedValue, surrogateRootKey, TestCancellationToken));

            Assert.Equal("rootKey", protectException.ParamName);
            Assert.Equal("rootKey", unprotectException.ParamName);
        }

        Assert.Equal(
            "secret",
            protector.Unprotect(protectedValue, foldedRootKey, TestCancellationToken));
    }

    [Fact]
    public void DistinctWellFormedPurposeAndRootKey_StillFailAuthentication()
    {
        var protectedValue = CreateProtector().Protect("secret", RootKey, TestCancellationToken);
        var otherPurpose = new SensitiveValueProtector(TestService, "configuration.api.token");
        var otherRootKey = "another-well-formed-root-key-with-entropy-476";

        var purposeException = Assert.Throws<SensitiveValueProtectionException>(
            () => otherPurpose.Unprotect(protectedValue, RootKey, TestCancellationToken));
        var rootKeyException = Assert.Throws<SensitiveValueProtectionException>(
            () => CreateProtector().Unprotect(protectedValue, otherRootKey, TestCancellationToken));

        Assert.Equal(
            WellKnownSensitiveValueProtectionErrorCodes.AuthenticationFailed,
            purposeException.ErrorCode);
        Assert.Equal(
            WellKnownSensitiveValueProtectionErrorCodes.AuthenticationFailed,
            rootKeyException.ErrorCode);
    }

    [Theory]
    [InlineData("")]
    [InlineData("ascii-only-value")]
    [InlineData("\u4E2D\u6587\u503C-\u5BC6\u94A5")]
    [InlineData("emoji-\uD83D\uDE00-pair")]
    [InlineData("real-replacement-\uFFFD-char")]
    [InlineData("nul-\u0000-value")]
    public void ProtectAndUnprotect_WithWellFormedUnicode_RoundTrips(string plaintext)
    {
        var protector = CreateProtector();

        var protectedValue = protector.Protect(plaintext, RootKey, TestCancellationToken);
        var result = protector.Unprotect(protectedValue, RootKey, TestCancellationToken);

        Assert.Equal(plaintext, result);
    }

    [Theory]
    [InlineData("root-key-\uD83D\uDE00-with-enough-entropy-476")]
    [InlineData("root-key-\uFFFD-with-enough-entropy-476")]
    [InlineData("root-key-\u4E2D\u6587-with-enough-entropy-476")]
    public void ProtectAndUnprotect_WithWellFormedUnicodeRootKey_RoundTrips(string rootKey)
    {
        var protector = CreateProtector();

        var protectedValue = protector.Protect("secret", rootKey, TestCancellationToken);
        var result = protector.Unprotect(protectedValue, rootKey, TestCancellationToken);

        Assert.Equal("secret", result);
    }

    [Fact]
    public void Purpose_TrimAndLengthRules_ArePreserved()
    {
        var trimmed = new SensitiveValueProtector(TestService, "  configuration.database.password  ");
        Assert.Equal("configuration.database.password", trimmed.Purpose);

        var pairPurpose = new SensitiveValueProtector(TestService, " p-\uD83D\uDE00 ");
        Assert.Equal("p-\uD83D\uDE00", pairPurpose.Purpose);
        var protectedValue = pairPurpose.Protect("secret", RootKey, TestCancellationToken);
        Assert.Equal("secret", pairPurpose.Unprotect(protectedValue, RootKey, TestCancellationToken));

        Assert.Throws<ArgumentException>(() => new SensitiveValueProtector(TestService, "   "));
        Assert.Throws<ArgumentException>(() => new SensitiveValueProtector(TestService, new string('a', 129)));
        Assert.Equal(128, new SensitiveValueProtector(TestService, new string('a', 128)).Purpose.Length);
    }

    [Theory]
    [MemberData(nameof(MalformedUtf16Samples))]
    public void ProtectAndUnprotect_WhenAlreadyCancelled_DeliverCallerTokenBeforeUnicodeRejection(string malformed)
    {
        var protector = CreateProtector();
        var protectedValue = protector.Protect("secret", RootKey, TestCancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var protectPlaintext = Assert.Throws<OperationCanceledException>(
            () => protector.Protect(malformed, RootKey, cancellation.Token));
        var protectRootKey = Assert.Throws<OperationCanceledException>(
            () => protector.Protect("secret", malformed, cancellation.Token));
        var unprotectRootKey = Assert.Throws<OperationCanceledException>(
            () => protector.Unprotect(protectedValue, malformed, cancellation.Token));

        foreach (var exception in new[] { protectPlaintext, protectRootKey, unprotectRootKey })
        {
            Assert.Equal(cancellation.Token, exception.CancellationToken);
            Assert.Null(exception.InnerException);
            Assert.DoesNotContain(malformed, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(RootKey, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task SharedProtector_ConcurrentValidAndInvalidCalls_AreIndependent()
    {
        var protector = CreateProtector();
        var operations = Enumerable.Range(0, 64).Select(async index =>
        {
            await Task.Yield();
            if (index % 2 == 0)
            {
                var plaintext = $"secret-{index}";
                var protectedValue = protector.Protect(plaintext, RootKey, TestCancellationToken);
                Assert.Equal(
                    plaintext,
                    protector.Unprotect(protectedValue, RootKey, TestCancellationToken));
            }
            else
            {
                var exception = Assert.Throws<ArgumentException>(
                    () => protector.Protect($"bad-\uD800-{index}", RootKey, TestCancellationToken));
                Assert.Equal("plaintext", exception.ParamName);
                Assert.StartsWith(MalformedUtf16Message, exception.Message, StringComparison.Ordinal);
            }
        });

        await Task.WhenAll(operations);
    }

    private static SensitiveValueProtector CreateProtector() =>
        new(TestService, "configuration.database.password");

    private static CancellationToken TestCancellationToken =>
        TestContext.Current.CancellationToken;
}
