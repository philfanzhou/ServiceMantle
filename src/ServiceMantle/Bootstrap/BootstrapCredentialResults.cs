namespace ServiceMantle.Bootstrap;

/// <summary>Well-known safe error codes for Bootstrap credential operations.</summary>
/// <remarks>
/// No code or public message ever carries the plaintext credential, the digest, a file path, a
/// stored timestamp, or a provider exception. Every ordinary rejection of a candidate collapses into
/// <see cref="Invalid"/> so that a caller cannot tell an expired credential from a malformed,
/// mismatched, already consumed, or never provisioned one.
/// </remarks>
public static class WellKnownBootstrapCredentialErrorCodes
{
    /// <summary>
    /// The candidate is malformed, does not match, has expired, was already consumed, or no
    /// credential is provisioned at all.
    /// </summary>
    public const string Invalid = "bootstrap_credential.invalid";

    /// <summary>
    /// The credential store is corrupt, oversized, inaccessible, or failed with an I/O error.
    /// </summary>
    public const string Unavailable = "bootstrap_credential.unavailable";

    /// <summary>A credential is already provisioned and was not replaced.</summary>
    public const string AlreadyExists = "bootstrap_credential.already_exists";

    /// <summary>
    /// The Bootstrap file already exists, so a first-creation credential is never provisioned.
    /// </summary>
    public const string BootstrapConfigured = "bootstrap_credential.bootstrap_configured";

    private static readonly HashSet<string> DefinedCodes = new(StringComparer.Ordinal)
    {
        Invalid,
        Unavailable,
        AlreadyExists,
        BootstrapConfigured,
    };

    /// <summary>Determines whether a value is one of the codes declared by this type.</summary>
    /// <remarks>
    /// The comparison is exact and ordinal. Every Bootstrap credential rejection accepts only these
    /// codes, so no caller-supplied string - a candidate credential included - can reach a public
    /// <c>ErrorCode</c> or <c>ToString()</c>.
    /// </remarks>
    public static bool IsDefined(string? errorCode) =>
        errorCode is not null && DefinedCodes.Contains(errorCode);

    internal static string EnsureDefined(string errorCode, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(errorCode, parameterName);
        if (!IsDefined(errorCode))
        {
            throw new ArgumentException(
                "A Bootstrap credential rejection must use a code declared by " +
                $"{nameof(WellKnownBootstrapCredentialErrorCodes)}. Rejection classifications are a " +
                "closed set so that no caller-supplied value - a candidate credential has exactly " +
                "the shape of a plausible free-text code - can reach a public error code.",
                parameterName);
        }

        return errorCode;
    }
}

/// <summary>The closed result of provisioning a Bootstrap creation credential.</summary>
/// <remarks>
/// The plaintext is present only on the successful result that produced it, and only after the
/// credential record was durably written. A rejected operation never carries a plaintext, and
/// neither <see cref="ToString"/> nor the error code reveals the credential, the digest, or a path.
/// </remarks>
public sealed class BootstrapCredentialProvisionResult
{
    private BootstrapCredentialProvisionResult(
        BootstrapCredential? credential,
        DateTime? issuedAtUtc,
        DateTime? expiresAtUtc,
        string? errorCode)
    {
        Credential = credential;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        ErrorCode = errorCode;
    }

    /// <summary>Gets a value indicating whether a credential was provisioned and stored.</summary>
    public bool IsProvisioned => Credential is not null;

    /// <summary>Gets the plaintext credential, returned only by this successful result.</summary>
    public BootstrapCredential? Credential { get; }

    /// <summary>Gets the issuance timestamp of a provisioned credential.</summary>
    public DateTime? IssuedAtUtc { get; }

    /// <summary>Gets the expiry of a provisioned credential.</summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>Gets the safe error code of a rejected operation.</summary>
    public string? ErrorCode { get; }

    /// <summary>Creates a provisioned result.</summary>
    /// <exception cref="ArgumentNullException"><paramref name="credential"/> is null.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The expiry is not after the issuance.</exception>
    public static BootstrapCredentialProvisionResult Provisioned(
        BootstrapCredential credential,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc)
    {
        ArgumentNullException.ThrowIfNull(credential);
        if (expiresAtUtc <= issuedAtUtc)
        {
            throw new ArgumentOutOfRangeException(nameof(expiresAtUtc));
        }

        return new BootstrapCredentialProvisionResult(
            credential,
            issuedAtUtc,
            expiresAtUtc,
            errorCode: null);
    }

    /// <summary>Creates a rejected result.</summary>
    /// <param name="errorCode">
    /// A classification declared by <see cref="WellKnownBootstrapCredentialErrorCodes"/>.
    /// </param>
    /// <exception cref="ArgumentException">The error code is not one of the declared codes.</exception>
    public static BootstrapCredentialProvisionResult Rejected(string errorCode)
    {
        WellKnownBootstrapCredentialErrorCodes.EnsureDefined(errorCode, nameof(errorCode));
        return new BootstrapCredentialProvisionResult(
            credential: null,
            issuedAtUtc: null,
            expiresAtUtc: null,
            errorCode);
    }

    /// <summary>Returns a safe projection that never includes the plaintext credential.</summary>
    public override string ToString() =>
        $"BootstrapCredentialProvisionResult(IsProvisioned={IsProvisioned}, ErrorCode={ErrorCode})";
}

/// <summary>The closed result of consuming a Bootstrap creation credential.</summary>
/// <remarks>
/// A consumed result means the stored record was atomically claimed and is gone. It is never
/// restored: a later validation, write, response, or process failure leaves the credential consumed
/// and requires an explicit new local provision.
/// </remarks>
public sealed class BootstrapCredentialConsumptionResult
{
    private static readonly BootstrapCredentialConsumptionResult ConsumedResult = new(errorCode: null);

    private BootstrapCredentialConsumptionResult(string? errorCode)
    {
        ErrorCode = errorCode;
    }

    /// <summary>Gets a value indicating whether the credential was consumed.</summary>
    public bool IsConsumed => ErrorCode is null;

    /// <summary>Gets the safe error code of a rejected consumption.</summary>
    public string? ErrorCode { get; }

    /// <summary>Creates a consumed result.</summary>
    public static BootstrapCredentialConsumptionResult Consumed() => ConsumedResult;

    /// <summary>Creates a rejected result.</summary>
    /// <param name="errorCode">
    /// A classification declared by <see cref="WellKnownBootstrapCredentialErrorCodes"/>.
    /// </param>
    /// <exception cref="ArgumentException">The error code is not one of the declared codes.</exception>
    public static BootstrapCredentialConsumptionResult Rejected(string errorCode)
    {
        WellKnownBootstrapCredentialErrorCodes.EnsureDefined(errorCode, nameof(errorCode));
        return new BootstrapCredentialConsumptionResult(errorCode);
    }

    /// <summary>Returns a safe projection that never includes the candidate or the digest.</summary>
    public override string ToString() =>
        $"BootstrapCredentialConsumptionResult(IsConsumed={IsConsumed}, ErrorCode={ErrorCode})";
}

/// <summary>The finite diagnosable state of the local Bootstrap credential record.</summary>
public enum BootstrapCredentialStatus
{
    /// <summary>No credential record exists.</summary>
    NotProvisioned = 0,

    /// <summary>A credential record exists and has not expired.</summary>
    Provisioned = 1,

    /// <summary>A credential record exists but has expired.</summary>
    Expired = 2,

    /// <summary>The record is corrupt, oversized, or inaccessible.</summary>
    Unavailable = 3,
}

/// <summary>The closed, read-only observation of the local Bootstrap credential record.</summary>
/// <remarks>
/// The observation exists so that the consume-first window is diagnosable by local operations. It
/// never carries the plaintext or the digest, never repairs a record, and never restores a consumed
/// credential.
/// </remarks>
public sealed class BootstrapCredentialStatusResult
{
    private BootstrapCredentialStatusResult(
        BootstrapCredentialStatus status,
        DateTime? issuedAtUtc,
        DateTime? expiresAtUtc,
        bool bootstrapConfigured)
    {
        Status = status;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
        BootstrapConfigured = bootstrapConfigured;
    }

    /// <summary>Gets the observed record status.</summary>
    public BootstrapCredentialStatus Status { get; }

    /// <summary>Gets the issuance timestamp of an existing record.</summary>
    public DateTime? IssuedAtUtc { get; }

    /// <summary>Gets the expiry of an existing record.</summary>
    public DateTime? ExpiresAtUtc { get; }

    /// <summary>Gets whether a Bootstrap file already exists for this service.</summary>
    public bool BootstrapConfigured { get; }

    /// <summary>Creates an observation for an existing record.</summary>
    public static BootstrapCredentialStatusResult Existing(
        BootstrapCredentialStatus status,
        DateTime issuedAtUtc,
        DateTime expiresAtUtc,
        bool bootstrapConfigured)
    {
        if (status is not (BootstrapCredentialStatus.Provisioned or BootstrapCredentialStatus.Expired))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new BootstrapCredentialStatusResult(
            status,
            issuedAtUtc,
            expiresAtUtc,
            bootstrapConfigured);
    }

    /// <summary>Creates an observation without timestamps.</summary>
    public static BootstrapCredentialStatusResult Absent(
        BootstrapCredentialStatus status,
        bool bootstrapConfigured)
    {
        if (status is not (BootstrapCredentialStatus.NotProvisioned or BootstrapCredentialStatus.Unavailable))
        {
            throw new ArgumentOutOfRangeException(nameof(status));
        }

        return new BootstrapCredentialStatusResult(
            status,
            issuedAtUtc: null,
            expiresAtUtc: null,
            bootstrapConfigured);
    }

    /// <summary>Returns a safe projection that never includes the credential or the digest.</summary>
    public override string ToString() =>
        $"BootstrapCredentialStatusResult(Status={Status}, BootstrapConfigured={BootstrapConfigured})";
}

/// <summary>
/// Provisions, observes, and atomically consumes the instance-local one-time Bootstrap creation
/// credential.
/// </summary>
/// <remarks>
/// The credential authorizes exactly one anonymous first Bootstrap creation. Provisioning is an
/// explicit local operations action: no ServiceMantle management endpoint issues or rotates it, and
/// it is never a Setup Code, a management cookie, a database credential, or a Bootstrap MasterKey.
/// Consumption is one-way. A caller that consumes before writing the Bootstrap file accepts that a
/// later failure leaves the credential consumed and requires an explicit new provision.
/// </remarks>
public interface IBootstrapCredentialStore
{
    /// <summary>Provisions one credential when none exists and Bootstrap is not configured.</summary>
    /// <param name="lifetime">The validated lifetime applied to the new credential.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    ValueTask<BootstrapCredentialProvisionResult> ProvisionAsync(
        BootstrapCredentialLifetime lifetime,
        CancellationToken cancellationToken = default);

    /// <summary>Reads the finite state of the credential record without changing it.</summary>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    ValueTask<BootstrapCredentialStatusResult> GetStatusAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Validates and atomically consumes the credential matching a candidate.</summary>
    /// <param name="candidate">The caller-supplied plaintext candidate.</param>
    /// <param name="cancellationToken">The caller's cancellation token.</param>
    /// <remarks>
    /// An invalid candidate never consumes a valid credential. At most one caller, in this or any
    /// other process sharing the record, observes a consumed result.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancelled the operation.</exception>
    ValueTask<BootstrapCredentialConsumptionResult> ConsumeAsync(
        string? candidate,
        CancellationToken cancellationToken = default);
}
