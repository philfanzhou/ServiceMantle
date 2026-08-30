namespace ServiceMantle.Management;

/// <summary>
/// The closed outcome of parsing a <see cref="System.Security.Claims.ClaimsPrincipal"/>.
/// </summary>
public enum ManagementClaimsParseStatus
{
    /// <summary>
    /// A single legitimate operator identity was resolved.
    /// </summary>
    Parsed = 0,

    /// <summary>
    /// The principal carries no authenticated identity.
    /// </summary>
    Unauthenticated = 1,

    /// <summary>
    /// The principal is authenticated but its ServiceMantle claims are not acceptable.
    /// </summary>
    Invalid = 2,
}

/// <summary>
/// The closed result of <see cref="IManagementClaimsParser.Parse"/>.
/// </summary>
public sealed class ManagementClaimsParseResult
{
    private static readonly ManagementClaimsParseResult UnauthenticatedResult =
        new(ManagementClaimsParseStatus.Unauthenticated, identity: null, errorCode: null);

    private ManagementClaimsParseResult(
        ManagementClaimsParseStatus status,
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
    public ManagementClaimsParseStatus Status { get; }

    /// <summary>
    /// Gets the resolved identity when <see cref="Status"/> is
    /// <see cref="ManagementClaimsParseStatus.Parsed"/>.
    /// </summary>
    public ManagementIdentity? Identity { get; }

    /// <summary>
    /// Gets the safe error code when <see cref="Status"/> is
    /// <see cref="ManagementClaimsParseStatus.Invalid"/>.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Creates a parsed result.
    /// </summary>
    public static ManagementClaimsParseResult Parsed(ManagementIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new ManagementClaimsParseResult(
            ManagementClaimsParseStatus.Parsed,
            identity,
            errorCode: null);
    }

    /// <summary>
    /// Creates an unauthenticated result.
    /// </summary>
    public static ManagementClaimsParseResult Unauthenticated() => UnauthenticatedResult;

    /// <summary>
    /// Creates an invalid-claims result carrying a safe error code.
    /// </summary>
    /// <param name="errorCode">
    /// A classification declared by <see cref="WellKnownManagementIdentityErrorCodes"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The error code is not one of the declared classifications. Rejecting a claims principal is a
    /// ServiceMantle-owned decision, so the code is restricted to the closed set rather than to a
    /// character shape: a shape rule alone would let a caller place a credential fragment or a claim
    /// value into the public <see cref="ErrorCode"/> and <see cref="ToString"/>.
    /// </exception>
    public static ManagementClaimsParseResult Invalid(string errorCode) => new(
        ManagementClaimsParseStatus.Invalid,
        identity: null,
        ManagementIdentityErrorCode.EnsureWellKnown(errorCode, nameof(errorCode)));

    /// <summary>
    /// Returns a safe projection that never includes claim values.
    /// </summary>
    public override string ToString() =>
        $"ManagementClaimsParseResult(Status={Status}, ErrorCode={ErrorCode})";
}
