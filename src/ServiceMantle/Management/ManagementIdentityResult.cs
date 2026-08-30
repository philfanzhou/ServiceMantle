namespace ServiceMantle.Management;

/// <summary>
/// The closed three-state outcome of a management identity provider call.
/// </summary>
public enum ManagementIdentityStatus
{
    /// <summary>
    /// The provider obtained and verified credentials.
    /// </summary>
    Authenticated = 0,

    /// <summary>
    /// The provider worked correctly but found no acceptable credentials.
    /// </summary>
    Unauthenticated = 1,

    /// <summary>
    /// The provider or an upstream identity system failed.
    /// </summary>
    Failed = 2,
}

/// <summary>
/// The closed result of <see cref="IManagementIdentityProvider.GetIdentityAsync"/>.
/// </summary>
/// <remarks>
/// An expected provider failure must be reported as <see cref="ManagementIdentityStatus.Failed"/> and
/// never disguised as <see cref="ManagementIdentityStatus.Unauthenticated"/>. Results never retain an
/// original exception, credential, or upstream response.
/// </remarks>
public sealed class ManagementIdentityResult
{
    private static readonly ManagementIdentityResult UnauthenticatedResult =
        new(ManagementIdentityStatus.Unauthenticated, identity: null, errorCode: null);

    private ManagementIdentityResult(
        ManagementIdentityStatus status,
        ManagementIdentity? identity,
        string? errorCode)
    {
        Status = status;
        Identity = identity;
        ErrorCode = errorCode;
    }

    /// <summary>
    /// Gets the outcome classification.
    /// </summary>
    public ManagementIdentityStatus Status { get; }

    /// <summary>
    /// Gets the resolved identity when <see cref="Status"/> is
    /// <see cref="ManagementIdentityStatus.Authenticated"/>.
    /// </summary>
    public ManagementIdentity? Identity { get; }

    /// <summary>
    /// Gets the safe error code when <see cref="Status"/> is
    /// <see cref="ManagementIdentityStatus.Failed"/>.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Creates an authenticated result.
    /// </summary>
    public static ManagementIdentityResult Authenticated(ManagementIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new ManagementIdentityResult(
            ManagementIdentityStatus.Authenticated,
            identity,
            errorCode: null);
    }

    /// <summary>
    /// Creates an unauthenticated result.
    /// </summary>
    public static ManagementIdentityResult Unauthenticated() => UnauthenticatedResult;

    /// <summary>
    /// Creates a failed result carrying a safe error code.
    /// </summary>
    /// <remarks>
    /// Unlike the ServiceMantle-owned rejection results, this code originates in the consuming
    /// service's own provider, which classifies its own upstream failures. It is therefore held to
    /// the safe character shape rather than to
    /// <see cref="WellKnownManagementIdentityErrorCodes"/>, and supplying a classification instead
    /// of exception text, a credential, or an upstream response is the provider's obligation.
    /// ServiceMantle itself only ever produces
    /// <see cref="WellKnownManagementIdentityErrorCodes.ProviderFailed"/> here, through
    /// <see cref="ManagementIdentityProviderInvoker"/>.
    /// </remarks>
    /// <exception cref="ArgumentException">
    /// The error code is not 1-64 ASCII letters, digits, <c>.</c>, <c>_</c>, or <c>-</c>.
    /// </exception>
    public static ManagementIdentityResult Failed(string errorCode) => new(
        ManagementIdentityStatus.Failed,
        identity: null,
        ManagementIdentityErrorCode.EnsureValid(errorCode, nameof(errorCode)));

    /// <summary>
    /// Returns a safe projection that never includes operator or credential content.
    /// </summary>
    public override string ToString() =>
        $"ManagementIdentityResult(Status={Status}, ErrorCode={ErrorCode})";
}
