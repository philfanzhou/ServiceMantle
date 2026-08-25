using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;

namespace ServiceMantle.Configuration;

/// <summary>
/// Protects sensitive configuration values using an external root key and a bound service context.
/// </summary>
/// <remarks>
/// Instances are stateless and thread-safe. Root keys are supplied per operation so the protector
/// does not retain external key material. The current envelope uses HKDF-SHA-256 and AES-256-GCM.
/// </remarks>
public sealed class SensitiveValueProtector
{
    private const string EnvelopePrefix = "sm:v1:";
    private const string VersionMarker = "sm:v";
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const int DerivedKeySize = 32;
    private const int MaximumPurposeLength = 128;

    private static readonly byte[] DerivationSalt =
        "ServiceMantle/SensitiveValueProtector/v1/root"u8.ToArray();

    private readonly byte[] derivationInfo;
    private readonly byte[] associatedData;

    /// <summary>
    /// Initializes a protector bound to one service and one non-secret purpose.
    /// </summary>
    /// <param name="serviceId">The service whose sensitive value is being protected.</param>
    /// <param name="purpose">A non-secret identifier for the value's use.</param>
    /// <exception cref="ArgumentNullException">An argument is null.</exception>
    /// <exception cref="ArgumentException">The purpose is empty or too long.</exception>
    public SensitiveValueProtector(ServiceId serviceId, string purpose)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(purpose);

        var normalizedPurpose = purpose.Trim();
        if (normalizedPurpose.Length is 0 or > MaximumPurposeLength)
        {
            throw new ArgumentException(
                $"The protection purpose must contain between 1 and {MaximumPurposeLength} characters.",
                nameof(purpose));
        }

        ServiceId = serviceId;
        Purpose = normalizedPurpose;
        derivationInfo = BuildContext("key", serviceId.Value, normalizedPurpose);
        associatedData = BuildContext("aad", serviceId.Value, normalizedPurpose);
    }

    /// <summary>
    /// Gets the service identifier bound to this protector.
    /// </summary>
    public ServiceId ServiceId { get; }

    /// <summary>
    /// Gets the non-secret purpose bound to this protector.
    /// </summary>
    public string Purpose { get; }

    /// <summary>
    /// Encrypts and authenticates a sensitive value using the supplied external root key.
    /// </summary>
    /// <param name="plaintext">The sensitive value. An empty value is supported.</param>
    /// <param name="rootKey">The external root key loaded from Bootstrap.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>A versioned protected-value envelope.</returns>
    public string Protect(
        string plaintext,
        string rootKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(plaintext);
        ValidateRootKey(rootKey);
        cancellationToken.ThrowIfCancellationRequested();

        byte[]? rootKeyBytes = null;
        byte[]? plaintextBytes = null;
        byte[]? derivedKey = null;

        try
        {
            rootKeyBytes = Encoding.UTF8.GetBytes(rootKey);
            plaintextBytes = Encoding.UTF8.GetBytes(plaintext);
            derivedKey = DeriveKey(rootKeyBytes, derivationInfo);
            cancellationToken.ThrowIfCancellationRequested();

            var nonce = RandomNumberGenerator.GetBytes(NonceSize);
            var ciphertext = new byte[plaintextBytes.Length];
            var tag = new byte[TagSize];

            using (var aes = new AesGcm(derivedKey, TagSize))
            {
                aes.Encrypt(nonce, plaintextBytes, ciphertext, tag, associatedData);
            }

            cancellationToken.ThrowIfCancellationRequested();

            var payload = new byte[NonceSize + TagSize + ciphertext.Length];
            nonce.CopyTo(payload, 0);
            tag.CopyTo(payload, NonceSize);
            ciphertext.CopyTo(payload, NonceSize + TagSize);

            return EnvelopePrefix + Convert.ToBase64String(payload);
        }
        finally
        {
            Clear(rootKeyBytes);
            Clear(plaintextBytes);
            Clear(derivedKey);
        }
    }

    /// <summary>
    /// Authenticates and decrypts a protected sensitive value using the supplied external root key.
    /// </summary>
    /// <param name="protectedValue">The versioned protected-value envelope.</param>
    /// <param name="rootKey">The external root key loaded from Bootstrap.</param>
    /// <param name="cancellationToken">The cancellation token for the operation.</param>
    /// <returns>The original sensitive value.</returns>
    /// <exception cref="SensitiveValueProtectionException">
    /// The envelope is invalid, unsupported, or cannot be authenticated in this context.
    /// </exception>
    public string Unprotect(
        string protectedValue,
        string rootKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(protectedValue);
        ValidateRootKey(rootKey);
        cancellationToken.ThrowIfCancellationRequested();

        var encodedPayload = ReadEncodedPayload(protectedValue);
        byte[] payload;
        try
        {
            payload = Convert.FromBase64String(encodedPayload);
        }
        catch (FormatException)
        {
            throw InvalidCiphertext();
        }

        if (payload.Length < NonceSize + TagSize)
        {
            throw InvalidCiphertext();
        }

        byte[]? rootKeyBytes = null;
        byte[]? derivedKey = null;
        byte[]? plaintextBytes = null;

        try
        {
            rootKeyBytes = Encoding.UTF8.GetBytes(rootKey);
            derivedKey = DeriveKey(rootKeyBytes, derivationInfo);
            cancellationToken.ThrowIfCancellationRequested();

            var ciphertextLength = payload.Length - NonceSize - TagSize;
            plaintextBytes = new byte[ciphertextLength];

            using (var aes = new AesGcm(derivedKey, TagSize))
            {
                try
                {
                    aes.Decrypt(
                        payload.AsSpan(0, NonceSize),
                        payload.AsSpan(NonceSize + TagSize),
                        payload.AsSpan(NonceSize, TagSize),
                        plaintextBytes,
                        associatedData);
                }
                catch (AuthenticationTagMismatchException)
                {
                    throw AuthenticationFailed();
                }
                catch (CryptographicException)
                {
                    throw AuthenticationFailed();
                }
            }

            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                return new UTF8Encoding(false, true).GetString(plaintextBytes);
            }
            catch (DecoderFallbackException)
            {
                throw AuthenticationFailed();
            }
        }
        finally
        {
            Clear(payload);
            Clear(rootKeyBytes);
            Clear(derivedKey);
            Clear(plaintextBytes);
        }
    }

    /// <summary>
    /// Returns safe context information without key or value material.
    /// </summary>
    public override string ToString() =>
        $"SensitiveValueProtector(ServiceId={ServiceId.Value}, Purpose={Purpose})";

    private static byte[] BuildContext(string kind, string serviceId, string purpose)
    {
        var domain = Encoding.UTF8.GetBytes($"ServiceMantle/SensitiveValueProtector/v1/{kind}");
        var service = Encoding.UTF8.GetBytes(serviceId);
        var purposeBytes = Encoding.UTF8.GetBytes(purpose);
        var result = new byte[
            sizeof(int) + domain.Length +
            sizeof(int) + service.Length +
            sizeof(int) + purposeBytes.Length];

        var offset = 0;
        offset = WriteLengthPrefixed(result, offset, domain);
        offset = WriteLengthPrefixed(result, offset, service);
        WriteLengthPrefixed(result, offset, purposeBytes);
        return result;
    }

    private static int WriteLengthPrefixed(byte[] destination, int offset, byte[] value)
    {
        BinaryPrimitives.WriteInt32BigEndian(destination.AsSpan(offset, sizeof(int)), value.Length);
        offset += sizeof(int);
        value.CopyTo(destination, offset);
        return offset + value.Length;
    }

    private static byte[] DeriveKey(byte[] inputKeyMaterial, byte[] info)
    {
        var pseudorandomKey = HMACSHA256.HashData(DerivationSalt, inputKeyMaterial);
        byte[]? expansionInput = null;

        try
        {
            expansionInput = new byte[info.Length + 1];
            info.CopyTo(expansionInput, 0);
            expansionInput[^1] = 1;

            var output = HMACSHA256.HashData(pseudorandomKey, expansionInput);
            if (output.Length != DerivedKeySize)
            {
                Clear(output);
                throw new CryptographicException("Sensitive-value key derivation failed.");
            }

            return output;
        }
        finally
        {
            Clear(pseudorandomKey);
            Clear(expansionInput);
        }
    }

    private static string ReadEncodedPayload(string protectedValue)
    {
        if (protectedValue.StartsWith(EnvelopePrefix, StringComparison.Ordinal))
        {
            return protectedValue[EnvelopePrefix.Length..];
        }

        if (protectedValue.StartsWith(VersionMarker, StringComparison.Ordinal))
        {
            throw new SensitiveValueProtectionException(
                WellKnownSensitiveValueProtectionErrorCodes.UnsupportedVersion,
                "The sensitive-value envelope version is not supported.");
        }

        throw InvalidCiphertext();
    }

    private static void ValidateRootKey(string rootKey)
    {
        ArgumentNullException.ThrowIfNull(rootKey);
        if (string.IsNullOrWhiteSpace(rootKey))
        {
            throw new ArgumentException("The external root key cannot be empty.", nameof(rootKey));
        }
    }

    private static SensitiveValueProtectionException InvalidCiphertext() =>
        new(
            WellKnownSensitiveValueProtectionErrorCodes.InvalidCiphertext,
            "The sensitive-value envelope is invalid.");

    private static SensitiveValueProtectionException AuthenticationFailed() =>
        new(
            WellKnownSensitiveValueProtectionErrorCodes.AuthenticationFailed,
            "The sensitive value could not be authenticated.");

    private static void Clear(byte[]? value)
    {
        if (value is not null)
        {
            CryptographicOperations.ZeroMemory(value);
        }
    }
}
