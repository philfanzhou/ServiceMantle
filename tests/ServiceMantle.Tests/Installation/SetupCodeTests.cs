using System.Diagnostics;
using System.Reflection;
using ServiceMantle.Installation;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.Tests.Installation;

public sealed class SetupCodeTests
{
    private const string ZeroEntropyCode = "AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA";
    private const string ZeroEntropyDigest =
        "sha256-v1:22a48051594c1949deed7040850c1f0f8764537f5191be56732d16a54c1d8153";
    private const string SampleCode = "abcdefghijklmnopqrstuvwxyz012345";
    private const string SampleDigest =
        "sha256-v1:653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435c4";

    private static readonly DateTime CreatedAtUtc = new(2026, 01, 01, 00, 00, 00, DateTimeKind.Utc);

    [Fact]
    public void PublicContract_FixesTheCodeAndDigestShape()
    {
        Assert.Equal(32, SetupCode.Length);
        Assert.Equal(24, SetupCode.EntropyByteCount);
        Assert.Equal("sha256-v1:", SetupCodeDigest.Prefix);
        Assert.Equal(64, SetupCodeDigest.HexLength);
        Assert.Equal(74, SetupCodeDigest.ValueLength);
        Assert.Equal(TimeSpan.FromMinutes(30), SetupCodeLifetime.DefaultValue);
        Assert.Equal(TimeSpan.FromMinutes(5), SetupCodeLifetime.MinimumValue);
        Assert.Equal(TimeSpan.FromHours(24), SetupCodeLifetime.MaximumValue);

        Assert.Equal("installation.not_found", WellKnownSetupCodeErrorCodes.InstallationNotFound);
        Assert.Equal(
            "installation.state_invariant_violation",
            WellKnownSetupCodeErrorCodes.StateInvariantViolation);
        Assert.Equal("installation.completed", WellKnownSetupCodeErrorCodes.InstallationCompleted);
        Assert.Equal("installation.dirty_context", WellKnownSetupCodeErrorCodes.DirtyContext);
        Assert.Equal(
            "installation.concurrency_conflict",
            WellKnownSetupCodeErrorCodes.ConcurrencyConflict);
        Assert.Equal(
            "installation.setup_code_required",
            WellKnownSetupCodeErrorCodes.SetupCodeRequired);
        Assert.Equal("setup_code.already_exists", WellKnownSetupCodeErrorCodes.AlreadyExists);
        Assert.Equal("setup_code.not_created", WellKnownSetupCodeErrorCodes.NotCreated);
        Assert.Equal("setup_code.storage_corrupt", WellKnownSetupCodeErrorCodes.StorageCorrupt);
        Assert.Equal(
            "setup_code.generation_exhausted",
            WellKnownSetupCodeErrorCodes.GenerationExhausted);
        Assert.Equal("setup_code.invalid", WellKnownSetupCodeErrorCodes.Invalid);
        Assert.Equal("setup_code.expired", WellKnownSetupCodeErrorCodes.Expired);
    }

    [Fact]
    public void Generate_ProducesDistinctUnpaddedBase64UrlCodes()
    {
        var generated = Enumerable.Range(0, 128).Select(_ => SetupCode.Generate().Reveal()).ToArray();

        Assert.All(generated, code =>
        {
            Assert.Equal(SetupCode.Length, code.Length);
            Assert.All(code, character => Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '_' or '-'));
            Assert.True(SetupCode.TryParse(code, out _));
        });
        Assert.Equal(generated.Length, generated.Distinct(StringComparer.Ordinal).Count());
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123456", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234=", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234+", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz01234/", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz0123 5", false)]
    [InlineData(" bcdefghijklmnopqrstuvwxyz012345", false)]
    [InlineData("abcdefghijklmnopqrstuvwxyz012345", true)]
    [InlineData("ABCDEFGHIJKLMNOPQRSTUVWXYZ012345", true)]
    [InlineData("__------________----____01234567", true)]
    public void TryParse_AcceptsOnlyTheExactShape(string? candidate, bool expected)
    {
        Assert.Equal(expected, SetupCode.TryParse(candidate, out var setupCode));
        Assert.Equal(expected, setupCode is not null);
        if (expected)
        {
            Assert.Equal(candidate, setupCode!.Reveal());
        }
    }

    [Fact]
    public void SetupCode_NeverProjectsThePlaintext()
    {
        var setupCode = SetupCode.Generate();
        var plaintext = setupCode.Reveal();
        var debuggerDisplay = typeof(SetupCode)
            .GetCustomAttribute<DebuggerDisplayAttribute>()!
            .Value;

        Assert.IsAssignableFrom<ISensitiveLogValue>(setupCode);
        Assert.Equal("SetupCode(********)", setupCode.ToString());
        Assert.DoesNotContain(plaintext, setupCode.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(plaintext, debuggerDisplay, StringComparison.Ordinal);
        Assert.DoesNotContain("Reveal", debuggerDisplay, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(ZeroEntropyCode, ZeroEntropyDigest)]
    [InlineData(SampleCode, SampleDigest)]
    public void Compute_ProducesTheFixedVersionedDigest(string code, string expectedDigest)
    {
        Assert.True(SetupCode.TryParse(code, out var setupCode));

        var digest = SetupCodeDigest.Compute(setupCode!);

        Assert.Equal(expectedDigest, digest.Value);
        Assert.True(digest.Matches(setupCode!));
        Assert.Equal("SetupCodeDigest(sha256-v1)", digest.ToString());
        Assert.DoesNotContain(code, digest.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(expectedDigest, digest.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Matches_RejectsEveryOtherCandidate()
    {
        Assert.True(SetupCode.TryParse(SampleCode, out var setupCode));
        Assert.True(SetupCode.TryParse(ZeroEntropyCode, out var other));

        Assert.False(SetupCodeDigest.Compute(setupCode!).Matches(other!));
        Assert.Throws<ArgumentNullException>(() => SetupCodeDigest.Compute(null!));
        Assert.Throws<ArgumentNullException>(() =>
            SetupCodeDigest.Compute(setupCode!).Matches(null!));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData(SampleCode)]
    [InlineData("sha256-v2:653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435c4")]
    [InlineData("sha512-v1:653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435c4")]
    [InlineData("653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435c4")]
    [InlineData("sha256-v1:653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435c")]
    [InlineData("sha256-v1:653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435c44")]
    [InlineData("sha256-v1:653BB1245E828FCDA4FA53FCD5A3DEF5BD7654E651F54B4132B73D74E64435C4")]
    [InlineData("sha256-v1:653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435g4")]
    public void DigestTryParse_FailsClosedForUnknownVersionsAndMalformedValues(string? storedValue)
    {
        Assert.False(SetupCodeDigest.TryParse(storedValue, out var digest));
        Assert.Null(digest);
    }

    [Fact]
    public void Lifetime_EnforcesTheClosedConfigurationInterval()
    {
        Assert.Equal(TimeSpan.FromMinutes(30), SetupCodeLifetime.Default.Value);
        Assert.Equal(
            SetupCodeLifetime.MinimumValue,
            SetupCodeLifetime.Create(SetupCodeLifetime.MinimumValue).Value);
        Assert.Equal(
            SetupCodeLifetime.MaximumValue,
            SetupCodeLifetime.Create(SetupCodeLifetime.MaximumValue).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SetupCodeLifetime.Create(SetupCodeLifetime.MinimumValue - TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SetupCodeLifetime.Create(SetupCodeLifetime.MaximumValue + TimeSpan.FromSeconds(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SetupCodeLifetime.Create(TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SetupCodeLifetime.Create(TimeSpan.FromMinutes(-30)));
    }

    [Theory]
    // generation, digest, issuedAtOffsetMinutes, expiresAtOffsetMinutes, expected status
    [InlineData(0, null, null, null, SetupCodeMaterialStatus.NeverIssued)]
    [InlineData(1, SampleDigest, 0, 30, SetupCodeMaterialStatus.Issued)]
    [InlineData(int.MaxValue, SampleDigest, 10, 40, SetupCodeMaterialStatus.Issued)]
    [InlineData(-1, null, null, null, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(0, SampleDigest, null, null, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(0, null, 0, null, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(0, null, null, 30, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(0, SampleDigest, 0, 30, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, null, 0, 30, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, SampleDigest, null, 30, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, SampleDigest, 0, null, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, null, null, null, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, "sha256-v2:653bb1245e828fcda4fa53fcd5a3def5bd7654e651f54b4132b73d74e64435c4", 0, 30, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, "sha256-v1:not-hexadecimal", 0, 30, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, SampleDigest, 30, 30, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, SampleDigest, 30, 0, SetupCodeMaterialStatus.Corrupt)]
    [InlineData(1, SampleDigest, -1, 30, SetupCodeMaterialStatus.Corrupt)]
    public void Evaluate_AppliesTheGenerationInvariant(
        int generation,
        string? digest,
        int? issuedAtOffsetMinutes,
        int? expiresAtOffsetMinutes,
        SetupCodeMaterialStatus expected)
    {
        var material = SetupCodeMaterial.Evaluate(
            generation,
            digest,
            Offset(issuedAtOffsetMinutes),
            Offset(expiresAtOffsetMinutes),
            CreatedAtUtc);

        Assert.Equal(expected, material.Status);
        Assert.Equal(generation, material.Generation);
        if (expected != SetupCodeMaterialStatus.Issued)
        {
            Assert.Null(material.Digest);
            Assert.Null(material.IssuedAtUtc);
            Assert.Null(material.ExpiresAtUtc);
        }
    }

    [Fact]
    public void Evaluate_KeepsDeletedMaterialDistinguishableFromAFreshPendingRow()
    {
        // Dropping any single field must never make an already issued row look never-issued again.
        var issued = SetupCodeMaterial.Evaluate(
            1,
            SampleDigest,
            CreatedAtUtc,
            CreatedAtUtc.AddMinutes(30),
            CreatedAtUtc);

        Assert.Equal(SetupCodeMaterialStatus.Issued, issued.Status);
        Assert.All(
            new[]
            {
                SetupCodeMaterial.Evaluate(1, null, CreatedAtUtc, CreatedAtUtc.AddMinutes(30), CreatedAtUtc),
                SetupCodeMaterial.Evaluate(1, SampleDigest, null, CreatedAtUtc.AddMinutes(30), CreatedAtUtc),
                SetupCodeMaterial.Evaluate(1, SampleDigest, CreatedAtUtc, null, CreatedAtUtc),
                SetupCodeMaterial.Evaluate(1, null, null, null, CreatedAtUtc),
            },
            material => Assert.Equal(SetupCodeMaterialStatus.Corrupt, material.Status));
    }

    [Fact]
    public void IsExpired_TreatsTheExpiryInstantAsExpired()
    {
        var material = SetupCodeMaterial.Evaluate(
            1,
            SampleDigest,
            CreatedAtUtc,
            CreatedAtUtc.AddMinutes(30),
            CreatedAtUtc);

        Assert.False(material.IsExpired(CreatedAtUtc.AddMinutes(29).AddSeconds(59)));
        Assert.True(material.IsExpired(CreatedAtUtc.AddMinutes(30)));
        Assert.True(material.IsExpired(CreatedAtUtc.AddMinutes(31)));
        Assert.False(SetupCodeMaterial
            .Evaluate(0, null, null, null, CreatedAtUtc)
            .IsExpired(CreatedAtUtc.AddYears(1)));
        Assert.Equal(
            "SetupCodeMaterial(Status=Issued, Generation=1)",
            material.ToString());
        Assert.DoesNotContain(SampleDigest, material.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void Results_NeverCarryThePlaintextOrRawStoredValues()
    {
        var setupCode = SetupCode.Generate();
        var issued = SetupCodeIssueResult.Issued(setupCode, 1, CreatedAtUtc.AddMinutes(30));
        var rejected = SetupCodeIssueResult.Rejected(WellKnownSetupCodeErrorCodes.AlreadyExists);

        Assert.True(issued.IsIssued);
        Assert.Same(setupCode, issued.SetupCode);
        Assert.Equal(1, issued.Generation);
        Assert.Null(issued.ErrorCode);
        Assert.DoesNotContain(setupCode.Reveal(), issued.ToString(), StringComparison.Ordinal);

        Assert.False(rejected.IsIssued);
        Assert.Null(rejected.SetupCode);
        Assert.Equal(WellKnownSetupCodeErrorCodes.AlreadyExists, rejected.ErrorCode);

        Assert.True(SetupCodeValidationResult.Valid().IsValid);
        Assert.False(SetupCodeValidationResult
            .Rejected(WellKnownSetupCodeErrorCodes.Expired)
            .IsValid);
        Assert.Equal(
            "SetupCodeConsumptionResult(IsStaged=False, ErrorCode=setup_code.invalid)",
            SetupCodeConsumptionResult.Rejected(WellKnownSetupCodeErrorCodes.Invalid).ToString());

        Assert.Throws<ArgumentNullException>(() =>
            SetupCodeIssueResult.Issued(null!, 1, CreatedAtUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            SetupCodeIssueResult.Issued(setupCode, 0, CreatedAtUtc));
        Assert.Throws<ArgumentException>(() => SetupCodeIssueResult.Rejected(string.Empty));
        Assert.Throws<ArgumentException>(() => SetupCodeValidationResult.Rejected(string.Empty));
        Assert.Throws<ArgumentException>(() => SetupCodeConsumptionResult.Rejected(string.Empty));
        Assert.Throws<ArgumentNullException>(() => SetupCodeConsumptionResult.Staged(null!));
    }

    private static DateTime? Offset(int? minutes) =>
        minutes.HasValue ? CreatedAtUtc.AddMinutes(minutes.Value) : null;
}
