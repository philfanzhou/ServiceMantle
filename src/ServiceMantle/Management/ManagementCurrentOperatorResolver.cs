using System.Security.Claims;
using ServiceMantle.Audit;

namespace ServiceMantle.Management;

/// <summary>
/// The closed outcome of resolving the current management operator.
/// </summary>
public enum ManagementCurrentOperatorStatus
{
    /// <summary>
    /// A single legitimate operator was resolved.
    /// </summary>
    Resolved = 0,

    /// <summary>
    /// The principal carries no authenticated identity.
    /// </summary>
    Unauthenticated = 1,

    /// <summary>
    /// The principal is authenticated but its ServiceMantle claims are not acceptable.
    /// </summary>
    ClaimsInvalid = 2,
}

/// <summary>
/// The closed result of <see cref="IManagementCurrentOperatorResolver.Resolve"/>.
/// </summary>
/// <remarks>
/// Unauthenticated and claims-invalid stay distinguishable so authorization and auditing can treat
/// them differently, and neither carries exception text, claim values, or credentials.
/// </remarks>
public sealed class ManagementCurrentOperatorResult
{
    private static readonly ManagementCurrentOperatorResult UnauthenticatedResult =
        new(ManagementCurrentOperatorStatus.Unauthenticated, identity: null, errorCode: null);

    private ManagementCurrentOperatorResult(
        ManagementCurrentOperatorStatus status,
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
    public ManagementCurrentOperatorStatus Status { get; }

    /// <summary>
    /// Gets the resolved identity when <see cref="Status"/> is
    /// <see cref="ManagementCurrentOperatorStatus.Resolved"/>.
    /// </summary>
    public ManagementIdentity? Identity { get; }

    /// <summary>
    /// Gets the safe error code when <see cref="Status"/> is
    /// <see cref="ManagementCurrentOperatorStatus.ClaimsInvalid"/>.
    /// </summary>
    public string? ErrorCode { get; }

    /// <summary>
    /// Gets the audit operator projection of a resolved identity.
    /// </summary>
    public ManagementAuditOperator? Operator => Identity?.ToAuditOperator();

    /// <summary>
    /// Creates a resolved result.
    /// </summary>
    public static ManagementCurrentOperatorResult Resolved(ManagementIdentity identity)
    {
        ArgumentNullException.ThrowIfNull(identity);
        return new ManagementCurrentOperatorResult(
            ManagementCurrentOperatorStatus.Resolved,
            identity,
            errorCode: null);
    }

    /// <summary>
    /// Creates an unauthenticated result.
    /// </summary>
    public static ManagementCurrentOperatorResult Unauthenticated() => UnauthenticatedResult;

    /// <summary>
    /// Creates a claims-invalid result carrying a safe error code.
    /// </summary>
    /// <param name="errorCode">
    /// A classification declared by <see cref="WellKnownManagementIdentityErrorCodes"/>.
    /// </param>
    /// <exception cref="ArgumentException">
    /// The error code is not one of the declared classifications. Rejecting a claims principal is a
    /// ServiceMantle-owned decision, so the code is restricted to the closed set rather than to a
    /// character shape.
    /// </exception>
    public static ManagementCurrentOperatorResult ClaimsInvalid(string errorCode) => new(
        ManagementCurrentOperatorStatus.ClaimsInvalid,
        identity: null,
        ManagementIdentityErrorCode.EnsureWellKnown(errorCode, nameof(errorCode)));

    /// <summary>
    /// Returns a safe projection that never includes operator or claim content.
    /// </summary>
    public override string ToString() =>
        $"ManagementCurrentOperatorResult(Status={Status}, ErrorCode={ErrorCode})";
}

/// <summary>
/// Resolves the current management operator from an already authenticated principal.
/// </summary>
public interface IManagementCurrentOperatorResolver
{
    /// <summary>
    /// Resolves the current operator.
    /// </summary>
    /// <param name="principal">The principal to resolve; may be <see langword="null"/>.</param>
    /// <returns>A closed result; expected rejections are never expressed as exceptions.</returns>
    ManagementCurrentOperatorResult Resolve(ClaimsPrincipal? principal);
}

/// <summary>
/// The default current-operator resolver, built on <see cref="IManagementClaimsParser"/>.
/// </summary>
/// <remarks>
/// The resolver only consumes the authentication conclusion and legitimate claims of a principal. It
/// never calls an <see cref="IManagementIdentityProvider"/>; the provider three-state distinction
/// stays with the identity result and its invoker, and is mapped by the login or authentication
/// adapter that owns it.
/// </remarks>
public sealed class ManagementCurrentOperatorResolver : IManagementCurrentOperatorResolver
{
    private readonly IManagementClaimsParser parser;

    /// <summary>
    /// Initializes the resolver.
    /// </summary>
    public ManagementCurrentOperatorResolver(IManagementClaimsParser parser)
    {
        ArgumentNullException.ThrowIfNull(parser);
        this.parser = parser;
    }

    /// <inheritdoc />
    public ManagementCurrentOperatorResult Resolve(ClaimsPrincipal? principal)
    {
        var parsed = parser.Parse(principal);
        return parsed.Status switch
        {
            ManagementClaimsParseStatus.Parsed =>
                ManagementCurrentOperatorResult.Resolved(parsed.Identity!),
            ManagementClaimsParseStatus.Invalid =>
                ManagementCurrentOperatorResult.ClaimsInvalid(parsed.ErrorCode!),
            _ => ManagementCurrentOperatorResult.Unauthenticated(),
        };
    }
}
