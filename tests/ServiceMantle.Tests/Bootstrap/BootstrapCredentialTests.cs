using ServiceMantle.Bootstrap;
using ServiceMantle.Logging;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the Bootstrap creation credential value type, its versioned digest, its lifetime, and the
/// closed result projections.
/// </summary>
public sealed class BootstrapCredentialTests
{
    [Fact]
    public void Generated_credentials_use_the_fixed_entropy_length_and_character_set()
    {
        var credential = BootstrapCredential.Generate();
        var plaintext = credential.Reveal();

        // The entropy claim rests on using 32 bytes of the BCL CSPRNG and the exact encoding length
        // that follows from it, not on observing that generated values differ.
        Assert.Equal(32, BootstrapCredential.EntropyByteCount);
        Assert.Equal(43, BootstrapCredential.Length);
        Assert.Equal(BootstrapCredential.Length, plaintext.Length);
        Assert.All(plaintext, character => Assert.True(
            character is >= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9' or '_' or '-'));
    }

    [Fact]
    public void A_credential_is_a_sensitive_log_value_that_never_projects_its_plaintext()
    {
        var credential = BootstrapCredential.Generate();

        Assert.IsAssignableFrom<ISensitiveLogValue>(credential);
        Assert.Equal("BootstrapCredential(********)", credential.ToString());
        Assert.DoesNotContain(credential.Reveal(), credential.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("short")]
    [InlineData("+++++++++++++++++++++++++++++++++++++++++++")]
    [InlineData("///////////////////////////////////////////")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA=")]
    public void Malformed_candidates_are_rejected(string? candidate)
    {
        Assert.False(BootstrapCredential.TryParse(candidate, out var parsed));
        Assert.Null(parsed);
    }

    [Fact]
    public void Candidates_are_case_sensitive_and_never_trimmed()
    {
        var credential = BootstrapCredential.Generate();
        var digest = BootstrapCredentialDigest.Compute(credential);
        var plaintext = credential.Reveal();

        Assert.False(BootstrapCredential.TryParse(" " + plaintext[1..], out _));
        Assert.False(BootstrapCredential.TryParse(plaintext[..^1] + " ", out _));
        Assert.False(BootstrapCredential.TryParse(" " + plaintext + " ", out _));
        Assert.True(BootstrapCredential.TryParse(plaintext, out var same));
        Assert.True(digest.Matches(same!));

        var flipped = Flip(plaintext);
        Assert.True(BootstrapCredential.TryParse(flipped, out var other));
        Assert.False(digest.Matches(other!));
    }

    [Fact]
    public void The_digest_is_versioned_fixed_length_and_compared_in_constant_time()
    {
        var credential = BootstrapCredential.Generate();
        var digest = BootstrapCredentialDigest.Compute(credential);

        // A fixed-length digest is what makes CryptographicOperations.FixedTimeEquals meaningful:
        // both operands are always 32 bytes, so no comparison ends early on a length difference.
        Assert.StartsWith("sha256-v1:", digest.Value, StringComparison.Ordinal);
        Assert.Equal(BootstrapCredentialDigest.ValueLength, digest.Value.Length);
        Assert.Equal(74, digest.Value.Length);
        Assert.True(digest.Matches(credential));
        Assert.False(digest.Matches(BootstrapCredential.Generate()));
        Assert.Equal("BootstrapCredentialDigest(sha256-v1)", digest.ToString());
        Assert.DoesNotContain(credential.Reveal(), digest.Value, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("sha256-v2:0000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256-v1:000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256-v1:00000000000000000000000000000000000000000000000000000000000000000")]
    [InlineData("sha256-v1:00000000000000000000000000000000000000000000000000000000000000AB")]
    [InlineData("0000000000000000000000000000000000000000000000000000000000000000")]
    public void Malformed_stored_digests_are_rejected_before_any_comparison(string? storedValue)
    {
        Assert.False(BootstrapCredentialDigest.TryParse(storedValue, out var digest));
        Assert.Null(digest);
    }

    [Fact]
    public void A_well_formed_stored_digest_round_trips()
    {
        var credential = BootstrapCredential.Generate();
        var digest = BootstrapCredentialDigest.Compute(credential);

        Assert.True(BootstrapCredentialDigest.TryParse(digest.Value, out var parsed));
        Assert.True(parsed!.Matches(credential));
        Assert.False(parsed.Matches(BootstrapCredential.Generate()));
    }

    [Fact]
    public void The_lifetime_range_is_one_to_sixty_minutes_with_a_fifteen_minute_default()
    {
        Assert.Equal(TimeSpan.FromMinutes(1), BootstrapCredentialLifetime.MinimumValue);
        Assert.Equal(TimeSpan.FromMinutes(60), BootstrapCredentialLifetime.MaximumValue);
        Assert.Equal(TimeSpan.FromMinutes(15), BootstrapCredentialLifetime.DefaultValue);
        Assert.Equal(TimeSpan.FromMinutes(15), BootstrapCredentialLifetime.Default.Value);
        Assert.Equal(
            TimeSpan.FromMinutes(1),
            BootstrapCredentialLifetime.Create(TimeSpan.FromMinutes(1)).Value);
        Assert.Equal(
            TimeSpan.FromMinutes(60),
            BootstrapCredentialLifetime.Create(TimeSpan.FromMinutes(60)).Value);
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BootstrapCredentialLifetime.Create(TimeSpan.FromSeconds(59)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BootstrapCredentialLifetime.Create(TimeSpan.FromMinutes(61)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BootstrapCredentialLifetime.Create(TimeSpan.Zero));
    }

    [Fact]
    public void Rejection_codes_are_a_closed_set()
    {
        Assert.True(WellKnownBootstrapCredentialErrorCodes.IsDefined("bootstrap_credential.invalid"));
        Assert.True(WellKnownBootstrapCredentialErrorCodes.IsDefined("bootstrap_credential.unavailable"));
        Assert.True(WellKnownBootstrapCredentialErrorCodes.IsDefined("bootstrap_credential.already_exists"));
        Assert.True(WellKnownBootstrapCredentialErrorCodes.IsDefined(
            "bootstrap_credential.bootstrap_configured"));
        Assert.False(WellKnownBootstrapCredentialErrorCodes.IsDefined(null));
        Assert.False(WellKnownBootstrapCredentialErrorCodes.IsDefined("setup_code.invalid"));

        // A candidate credential has exactly the shape of a plausible free-text code, so the closed
        // set is what stops one from reaching a public error code.
        var candidate = BootstrapCredential.Generate().Reveal();
        Assert.Throws<ArgumentException>(() =>
            BootstrapCredentialConsumptionResult.Rejected(candidate));
        Assert.Throws<ArgumentException>(() =>
            BootstrapCredentialProvisionResult.Rejected(candidate));
    }

    [Fact]
    public void Results_project_only_finite_state()
    {
        var credential = BootstrapCredential.Generate();
        var issuedAtUtc = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        var provisioned = BootstrapCredentialProvisionResult.Provisioned(
            credential,
            issuedAtUtc,
            issuedAtUtc.AddMinutes(15));
        var rejected = BootstrapCredentialProvisionResult.Rejected(
            WellKnownBootstrapCredentialErrorCodes.AlreadyExists);
        var consumed = BootstrapCredentialConsumptionResult.Consumed();
        var status = BootstrapCredentialStatusResult.Existing(
            BootstrapCredentialStatus.Provisioned,
            issuedAtUtc,
            issuedAtUtc.AddMinutes(15),
            bootstrapConfigured: false);

        Assert.True(provisioned.IsProvisioned);
        Assert.Same(credential, provisioned.Credential);
        Assert.Null(provisioned.ErrorCode);
        Assert.False(rejected.IsProvisioned);
        Assert.Null(rejected.Credential);
        Assert.True(consumed.IsConsumed);
        Assert.DoesNotContain(credential.Reveal(), provisioned.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(
            BootstrapCredentialDigest.Compute(credential).Value,
            provisioned.ToString(),
            StringComparison.Ordinal);
        Assert.Equal(
            "BootstrapCredentialStatusResult(Status=Provisioned, BootstrapConfigured=False)",
            status.ToString());
        Assert.All(
            typeof(BootstrapCredentialProvisionResult).GetProperties(),
            property => Assert.Null(property.SetMethod));
        Assert.All(
            typeof(BootstrapCredentialStatusResult).GetProperties(),
            property => Assert.Null(property.SetMethod));
    }

    [Fact]
    public void Result_factories_reject_impossible_values()
    {
        var issuedAtUtc = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);

        Assert.Throws<ArgumentNullException>(() =>
            BootstrapCredentialProvisionResult.Provisioned(null!, issuedAtUtc, issuedAtUtc.AddMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BootstrapCredentialProvisionResult.Provisioned(
                BootstrapCredential.Generate(),
                issuedAtUtc,
                issuedAtUtc));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BootstrapCredentialStatusResult.Existing(
                BootstrapCredentialStatus.NotProvisioned,
                issuedAtUtc,
                issuedAtUtc.AddMinutes(1),
                bootstrapConfigured: false));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            BootstrapCredentialStatusResult.Absent(
                BootstrapCredentialStatus.Provisioned,
                bootstrapConfigured: false));
        Assert.Throws<ArgumentNullException>(() => BootstrapCredentialDigest.Compute(null!));
    }

    [Fact]
    public void The_credential_contract_stays_provider_and_framework_independent()
    {
        var references = typeof(IBootstrapCredentialStore).Assembly.GetReferencedAssemblies();

        Assert.Equal(typeof(BootstrapFileStore).Assembly, typeof(IBootstrapCredentialStore).Assembly);
        Assert.DoesNotContain(references, reference =>
            reference.Name?.StartsWith("Microsoft.AspNetCore", StringComparison.Ordinal) == true ||
            reference.Name?.StartsWith("Microsoft.EntityFrameworkCore", StringComparison.Ordinal) == true ||
            reference.Name?.StartsWith("Npgsql", StringComparison.Ordinal) == true ||
            reference.Name?.StartsWith("Microsoft.Data.", StringComparison.Ordinal) == true);
    }

    private static string Flip(string plaintext)
    {
        foreach (var (character, index) in plaintext.Select((value, index) => (value, index)))
        {
            if (char.IsAsciiLetter(character))
            {
                return string.Concat(
                    plaintext.AsSpan(0, index),
                    char.IsAsciiLetterUpper(character)
                        ? char.ToLowerInvariant(character).ToString()
                        : char.ToUpperInvariant(character).ToString(),
                    plaintext.AsSpan(index + 1));
            }
        }

        return plaintext;
    }
}
