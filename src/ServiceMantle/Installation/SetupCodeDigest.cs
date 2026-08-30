using System.Security.Cryptography;
using System.Text;

namespace ServiceMantle.Installation;

/// <summary>
/// The versioned, persisted digest of a Setup Code.
/// </summary>
/// <remarks>
/// The format is fixed as <see cref="Prefix"/> followed by 64 lowercase hexadecimal SHA-256
/// characters, computed over the exact UTF-8 bytes of the 32-character ASCII Setup Code. An unknown
/// version or a malformed stored value is storage corruption, never an ordinary invalid code.
/// </remarks>
public sealed class SetupCodeDigest
{
    /// <summary>
    /// The fixed digest version prefix.
    /// </summary>
    public const string Prefix = "sha256-v1:";

    /// <summary>
    /// The number of hexadecimal characters in the digest payload.
    /// </summary>
    public const int HexLength = 64;

    /// <summary>
    /// The total character length of a persisted digest value.
    /// </summary>
    public const int ValueLength = 10 + HexLength;

    private readonly byte[] hash;

    private SetupCodeDigest(byte[] hash, string value)
    {
        this.hash = hash;
        Value = value;
    }

    /// <summary>
    /// Gets the persisted digest value.
    /// </summary>
    public string Value { get; }

    /// <summary>
    /// Computes the digest of a Setup Code.
    /// </summary>
    public static SetupCodeDigest Compute(SetupCode setupCode)
    {
        ArgumentNullException.ThrowIfNull(setupCode);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(setupCode.Reveal()));
        return new SetupCodeDigest(hash, Prefix + Convert.ToHexStringLower(hash));
    }

    /// <summary>
    /// Attempts to parse a persisted digest value.
    /// </summary>
    /// <remarks>
    /// The version prefix and the exact length are checked before the hexadecimal payload is decoded,
    /// so an unknown version never falls through to a comparison.
    /// </remarks>
    public static bool TryParse(string? storedValue, out SetupCodeDigest? digest)
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

        // Every character has already been checked, so decoding cannot fail here.
        digest = new SetupCodeDigest(Convert.FromHexString(hexadecimal), storedValue);
        return true;
    }

    /// <summary>
    /// Determines in constant time whether a candidate produces this digest.
    /// </summary>
    public bool Matches(SetupCode candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        return CryptographicOperations.FixedTimeEquals(hash, Compute(candidate).hash);
    }

    /// <summary>
    /// Returns a safe projection that never includes the digest payload.
    /// </summary>
    public override string ToString() => "SetupCodeDigest(sha256-v1)";
}
