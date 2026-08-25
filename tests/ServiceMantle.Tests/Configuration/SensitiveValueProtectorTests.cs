using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class SensitiveValueProtectorTests
{
    private const string RootKey = "root-key-with-enough-entropy-for-tests-4fba32";
    private static readonly ServiceId TestService = ServiceId.Parse("orders-api");

    [Theory]
    [InlineData("")]
    [InlineData("database-password")]
    [InlineData("密钥值-🔐")]
    public void ProtectAndUnprotect_RoundTripsInSameContext(string plaintext)
    {
        var protector = CreateProtector();

        var protectedValue = protector.Protect(plaintext, RootKey, TestCancellationToken);
        var result = protector.Unprotect(protectedValue, RootKey, TestCancellationToken);

        Assert.StartsWith("sm:v1:", protectedValue, StringComparison.Ordinal);
        Assert.Equal(plaintext, result);
        if (plaintext.Length > 0)
        {
            Assert.DoesNotContain(plaintext, protectedValue, StringComparison.Ordinal);
        }
    }

    [Fact]
    public void Protect_UsesUniqueNonceForEachEnvelope()
    {
        var protector = CreateProtector();

        var first = protector.Protect("same-value", RootKey, TestCancellationToken);
        var second = protector.Protect("same-value", RootKey, TestCancellationToken);

        Assert.NotEqual(first, second);
    }

    [Fact]
    public void Unprotect_WithDifferentServiceId_FailsClosed()
    {
        var protectedValue = CreateProtector().Protect("secret", RootKey, TestCancellationToken);
        var otherService = new SensitiveValueProtector(
            ServiceId.Parse("billing-api"),
            "configuration.database.password");

        var exception = Assert.Throws<SensitiveValueProtectionException>(
            () => otherService.Unprotect(protectedValue, RootKey, TestCancellationToken));

        Assert.Equal(
            WellKnownSensitiveValueProtectionErrorCodes.AuthenticationFailed,
            exception.ErrorCode);
    }

    [Fact]
    public void Unprotect_WithDifferentPurpose_FailsClosed()
    {
        var protectedValue = CreateProtector().Protect("secret", RootKey, TestCancellationToken);
        var otherPurpose = new SensitiveValueProtector(TestService, "configuration.api.token");

        var exception = Assert.Throws<SensitiveValueProtectionException>(
            () => otherPurpose.Unprotect(protectedValue, RootKey, TestCancellationToken));

        Assert.Equal(
            WellKnownSensitiveValueProtectionErrorCodes.AuthenticationFailed,
            exception.ErrorCode);
    }

    [Fact]
    public void Unprotect_WithWrongRootKey_FailsWithoutLeakingInputs()
    {
        const string plaintext = "do-not-leak-this-plaintext";
        const string wrongKey = "do-not-leak-this-wrong-root-key";
        var protectedValue = CreateProtector().Protect(plaintext, RootKey, TestCancellationToken);

        var exception = Assert.Throws<SensitiveValueProtectionException>(
            () => CreateProtector().Unprotect(protectedValue, wrongKey, TestCancellationToken));
        var diagnostic = exception.ToString();

        Assert.Equal(
            WellKnownSensitiveValueProtectionErrorCodes.AuthenticationFailed,
            exception.ErrorCode);
        Assert.DoesNotContain(plaintext, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(wrongKey, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedValue, diagnostic, StringComparison.Ordinal);
    }

    [Fact]
    public void Unprotect_WithDamagedCiphertext_FailsClosed()
    {
        var protectedValue = CreateProtector().Protect("secret", RootKey, TestCancellationToken);
        var payload = Convert.FromBase64String(protectedValue["sm:v1:".Length..]);
        payload[^1] ^= 0x40;
        var damagedValue = "sm:v1:" + Convert.ToBase64String(payload);

        var exception = Assert.Throws<SensitiveValueProtectionException>(
            () => CreateProtector().Unprotect(damagedValue, RootKey, TestCancellationToken));

        Assert.Equal(
            WellKnownSensitiveValueProtectionErrorCodes.AuthenticationFailed,
            exception.ErrorCode);
    }

    [Theory]
    [InlineData("sm:v1:not-base64")]
    [InlineData("sm:v1:AA==")]
    [InlineData("")]
    [InlineData("unversioned")]
    public void Unprotect_WithMalformedEnvelope_ReturnsSafeError(string protectedValue)
    {
        var exception = Assert.Throws<SensitiveValueProtectionException>(
            () => CreateProtector().Unprotect(protectedValue, RootKey, TestCancellationToken));

        Assert.Equal(
            WellKnownSensitiveValueProtectionErrorCodes.InvalidCiphertext,
            exception.ErrorCode);
        if (protectedValue.Length > 0)
        {
            Assert.DoesNotContain(protectedValue, exception.ToString(), StringComparison.Ordinal);
        }
        Assert.DoesNotContain(RootKey, exception.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Unprotect_WithUnknownVersion_FailsClosed()
    {
        var exception = Assert.Throws<SensitiveValueProtectionException>(
            () => CreateProtector().Unprotect("sm:v2:AAAA", RootKey, TestCancellationToken));

        Assert.Equal(
            WellKnownSensitiveValueProtectionErrorCodes.UnsupportedVersion,
            exception.ErrorCode);
    }

    [Fact]
    public void NullAndEmptyArguments_AreRejectedWithoutEchoingValues()
    {
        var protector = CreateProtector();

        Assert.Throws<ArgumentNullException>(() => protector.Protect(null!, RootKey, TestCancellationToken));
        Assert.Throws<ArgumentNullException>(() => protector.Protect("secret", null!, TestCancellationToken));
        Assert.Throws<ArgumentException>(() => protector.Protect("secret", "  ", TestCancellationToken));
        Assert.Throws<ArgumentNullException>(() => protector.Unprotect(null!, RootKey, TestCancellationToken));
        Assert.Throws<ArgumentNullException>(() => protector.Unprotect("sm:v1:AAAA", null!, TestCancellationToken));
        Assert.Throws<ArgumentException>(() => protector.Unprotect("sm:v1:AAAA", "", TestCancellationToken));
    }

    [Fact]
    public void ProtectAndUnprotect_WhenAlreadyCancelled_DoNotProcessValues()
    {
        var protector = CreateProtector();
        var protectedValue = protector.Protect("secret", RootKey, TestCancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        Assert.Throws<OperationCanceledException>(
            () => protector.Protect("secret", RootKey, cancellation.Token));
        Assert.Throws<OperationCanceledException>(
            () => protector.Unprotect(protectedValue, RootKey, cancellation.Token));
    }

    [Fact]
    public async Task SharedProtector_IsSafeForConcurrentOperations()
    {
        var protector = CreateProtector();
        var operations = Enumerable.Range(0, 64).Select(async index =>
        {
            await Task.Yield();
            var plaintext = $"secret-{index}";
            var protectedValue = protector.Protect(plaintext, RootKey, TestCancellationToken);
            Assert.Equal(
                plaintext,
                protector.Unprotect(protectedValue, RootKey, TestCancellationToken));
        });

        await Task.WhenAll(operations);
    }

    [Fact]
    public void ToString_DoesNotContainKeyOrProtectedValueMaterial()
    {
        var protector = CreateProtector();
        var protectedValue = protector.Protect("secret", RootKey, TestCancellationToken);
        var diagnostic = protector.ToString();

        Assert.Contains(TestService.Value, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, diagnostic, StringComparison.Ordinal);
        Assert.DoesNotContain(protectedValue, diagnostic, StringComparison.Ordinal);
    }

    private static SensitiveValueProtector CreateProtector() =>
        new(TestService, "configuration.database.password");

    private static CancellationToken TestCancellationToken =>
        TestContext.Current.CancellationToken;
}
