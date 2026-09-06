using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using ServiceMantle.Logging;

namespace ServiceMantle.Bootstrap;

/// <summary>
/// A local, short-lived, one-time Bootstrap creation credential in plaintext.
/// </summary>
/// <remarks>
/// The plaintext is returned exactly once, by the successful provision result that produced it. It is
/// never persisted, and <see cref="ToString"/>, the debugger display, exceptions, result projections,
/// and log output never reveal it: only the explicit <see cref="Reveal"/> call does. A Bootstrap
/// credential is a 256-bit high-entropy random value, not a user password, and it is never a Setup
/// Code, a management cookie, a database password, or a Bootstrap MasterKey.
/// </remarks>
[DebuggerDisplay("BootstrapCredential(********)")]
public sealed class BootstrapCredential : ISensitiveLogValue
{
    /// <summary>The exact character length of a Bootstrap credential.</summary>
    public const int Length = 43;

    /// <summary>
    /// The number of cryptographically secure random bytes behind a generated credential.
    /// </summary>
    public const int EntropyByteCount = 32;

    private readonly string value;

    private BootstrapCredential(string value)
    {
        this.value = value;
    }

    /// <summary>
    /// Generates a credential from <see cref="EntropyByteCount"/> cryptographically secure random
    /// bytes, rendered as unpadded Base64URL.
    /// </summary>
    public static BootstrapCredential Generate()
    {
        Span<byte> entropy = stackalloc byte[EntropyByteCount];
        RandomNumberGenerator.Fill(entropy);
        return new BootstrapCredential(Base64Url.EncodeToString(entropy));
    }

    /// <summary>Attempts to accept a caller-supplied candidate.</summary>
    /// <remarks>
    /// The candidate must be exactly <see cref="Length"/> characters from <c>[A-Za-z0-9_-]</c>.
    /// Matching is case sensitive and the value is never trimmed or normalized.
    /// </remarks>
    public static bool TryParse(string? candidate, out BootstrapCredential? credential)
    {
        if (candidate is null || candidate.Length != Length)
        {
            credential = null;
            return false;
        }

        foreach (var character in candidate)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9') &&
                character is not ('_' or '-'))
            {
                credential = null;
                return false;
            }
        }

        credential = new BootstrapCredential(candidate);
        return true;
    }

    /// <summary>Returns the plaintext credential.</summary>
    /// <remarks>
    /// ServiceMantle guarantees only that its own persistence, exceptions, log value projections, and
    /// diagnostics never echo the plaintext. What a caller does with a revealed credential, and
    /// whether the value survives in process memory, is outside that boundary.
    /// </remarks>
    public string Reveal() => value;

    /// <summary>Returns a safe projection that never includes the plaintext.</summary>
    public override string ToString() => "BootstrapCredential(********)";
}

/// <summary>The versioned, persisted digest of a Bootstrap credential.</summary>
/// <remarks>
/// The format is fixed as <see cref="Prefix"/> followed by 64 lowercase hexadecimal SHA-256
/// characters computed over the exact UTF-8 bytes of the credential. An unknown version or a
/// malformed stored value is storage corruption, never an ordinary invalid candidate. The persisted
/// encoding is the only place a digest reaches disk.
/// </remarks>
public sealed class BootstrapCredentialDigest
{
    /// <summary>The fixed digest version prefix.</summary>
    public const string Prefix = "sha256-v1:";

    /// <summary>The number of hexadecimal characters in the digest payload.</summary>
    public const int HexLength = 64;

    /// <summary>The total character length of a persisted digest value.</summary>
    public const int ValueLength = 10 + HexLength;

    private readonly byte[] hash;

    private BootstrapCredentialDigest(byte[] hash, string value)
    {
        this.hash = hash;
        Value = value;
    }

    /// <summary>Gets the persisted digest value.</summary>
    public string Value { get; }

    /// <summary>Computes the digest of a credential.</summary>
    public static BootstrapCredentialDigest Compute(BootstrapCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(credential.Reveal()));
        return new BootstrapCredentialDigest(hash, Prefix + Convert.ToHexStringLower(hash));
    }

    /// <summary>Attempts to parse a persisted digest value.</summary>
    /// <remarks>
    /// The version prefix and the exact length are checked before the hexadecimal payload is
    /// decoded, so an unknown version never falls through to a comparison.
    /// </remarks>
    public static bool TryParse(string? storedValue, out BootstrapCredentialDigest? digest)
    {
        digest = null;
        if (storedValue is null ||
            storedValue.Length != ValueLength ||
            !storedValue.StartsWith(Prefix, StringComparison.Ordinal))
        {
            return false;
        }

        var hexadecimal = storedValue.AsSpan(Prefix.Length);
        foreach (var character in hexadecimal)
        {
            if (character is not (>= '0' and <= '9' or >= 'a' and <= 'f'))
            {
                return false;
            }
        }

        digest = new BootstrapCredentialDigest(Convert.FromHexString(hexadecimal), storedValue);
        return true;
    }

    /// <summary>Determines in constant time whether a candidate produces this digest.</summary>
    public bool Matches(BootstrapCredential candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return CryptographicOperations.FixedTimeEquals(hash, Compute(candidate).hash);
    }

    /// <summary>Returns a safe projection that never includes the digest payload.</summary>
    public override string ToString() => "BootstrapCredentialDigest(sha256-v1)";
}

/// <summary>The validated lifetime applied to a newly provisioned Bootstrap credential.</summary>
/// <remarks>
/// The configurable range is the closed interval from 1 minute to 60 minutes, and the default is
/// 15 minutes. An out-of-range configuration is a programming error, not a domain rejection.
/// </remarks>
public sealed class BootstrapCredentialLifetime
{
    /// <summary>The smallest configurable lifetime.</summary>
    public static readonly TimeSpan MinimumValue = TimeSpan.FromMinutes(1);

    /// <summary>The largest configurable lifetime.</summary>
    public static readonly TimeSpan MaximumValue = TimeSpan.FromMinutes(60);

    /// <summary>The lifetime applied when none is configured.</summary>
    public static readonly TimeSpan DefaultValue = TimeSpan.FromMinutes(15);

    private BootstrapCredentialLifetime(TimeSpan value)
    {
        Value = value;
    }

    /// <summary>Gets the default lifetime.</summary>
    public static BootstrapCredentialLifetime Default { get; } = new(DefaultValue);

    /// <summary>Gets the configured lifetime.</summary>
    public TimeSpan Value { get; }

    /// <summary>Creates a validated lifetime.</summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is outside the closed interval from <see cref="MinimumValue"/> to
    /// <see cref="MaximumValue"/>.
    /// </exception>
    public static BootstrapCredentialLifetime Create(TimeSpan value)
    {
        if (value < MinimumValue || value > MaximumValue)
        {
            throw new ArgumentOutOfRangeException(
                nameof(value),
                $"A Bootstrap credential lifetime must be between {MinimumValue} and {MaximumValue}.");
        }

        return new BootstrapCredentialLifetime(value);
    }

    /// <summary>Returns the configured lifetime.</summary>
    public override string ToString() => $"BootstrapCredentialLifetime({Value})";
}
