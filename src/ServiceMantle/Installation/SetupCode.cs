using System.Buffers.Text;
using System.Diagnostics;
using System.Security.Cryptography;
using ServiceMantle.Logging;

namespace ServiceMantle.Installation;

/// <summary>
/// A one-time installation Setup Code in plaintext.
/// </summary>
/// <remarks>
/// The plaintext is returned exactly once, by the successful Create or Rotate result that produced it.
/// It is never persisted, and <see cref="ToString"/>, the debugger display, exceptions, result
/// projections, and log output never reveal it: only the explicit <see cref="Reveal"/> call does.
/// A Setup Code is a 192-bit high-entropy random value, not a user password, and the scheme does not
/// claim confidentiality once both the database and process memory are compromised.
/// </remarks>
[DebuggerDisplay("SetupCode(********)")]
public sealed class SetupCode : ISensitiveLogValue
{
    /// <summary>
    /// The exact character length of a Setup Code.
    /// </summary>
    public const int Length = 32;

    /// <summary>
    /// The number of cryptographically secure random bytes behind a generated Setup Code.
    /// </summary>
    public const int EntropyByteCount = 24;

    private readonly string value;

    private SetupCode(string value)
    {
        this.value = value;
    }

    /// <summary>
    /// Generates a Setup Code from <see cref="EntropyByteCount"/> cryptographically secure random
    /// bytes, rendered as unpadded Base64URL.
    /// </summary>
    public static SetupCode Generate()
    {
        Span<byte> entropy = stackalloc byte[EntropyByteCount];
        RandomNumberGenerator.Fill(entropy);
        return new SetupCode(Base64Url.EncodeToString(entropy));
    }

    /// <summary>
    /// Attempts to accept a caller supplied candidate.
    /// </summary>
    /// <remarks>
    /// The candidate must be exactly <see cref="Length"/> characters from
    /// <c>[A-Za-z0-9_-]</c>. Matching is case sensitive and the value is never trimmed or normalized.
    /// </remarks>
    public static bool TryParse(string? candidate, out SetupCode? setupCode)
    {
        if (candidate is null || candidate.Length != Length)
        {
            setupCode = null;
            return false;
        }

        foreach (var character in candidate)
        {
            if (character is not (>= 'A' and <= 'Z' or >= 'a' and <= 'z' or >= '0' and <= '9')
                && character is not ('_' or '-'))
            {
                setupCode = null;
                return false;
            }
        }

        setupCode = new SetupCode(candidate);
        return true;
    }

    /// <summary>
    /// Returns the plaintext Setup Code.
    /// </summary>
    /// <remarks>
    /// ServiceMantle guarantees only that its own persistence, exceptions, log value projections, and
    /// diagnostics never echo the plaintext. What a caller does with a revealed code is outside that
    /// boundary.
    /// </remarks>
    public string Reveal() => value;

    /// <summary>
    /// Returns a safe projection that never includes the plaintext.
    /// </summary>
    public override string ToString() => "SetupCode(********)";
}
