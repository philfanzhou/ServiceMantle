using System.Security.Claims;
using ServiceMantle.Audit;

namespace ServiceMantle.Management;

/// <summary>
/// Parses a <see cref="ClaimsPrincipal"/> into a management operator identity.
/// </summary>
public interface IManagementClaimsParser
{
    /// <summary>
    /// Parses the ServiceMantle management claims of a principal.
    /// </summary>
    /// <param name="principal">The principal to parse; may be <see langword="null"/>.</param>
    /// <returns>A closed result; expected rejections are never expressed as exceptions.</returns>
    ManagementClaimsParseResult Parse(ClaimsPrincipal? principal);
}

/// <summary>
/// The fail-closed ServiceMantle management claims parser.
/// </summary>
/// <remarks>
/// Claim types and claim values are matched as an exact ordinal wire contract; unknown claim types
/// are ignored and never contribute to the identity. The principal must carry exactly one
/// authenticated identity and all ServiceMantle operator and permission claims must live on that
/// identity, so a synthetic operator can never be assembled from several identities. An additional
/// unauthenticated identity that carries no ServiceMantle claim takes no part in parsing.
/// </remarks>
public sealed class ManagementClaimsParser : IManagementClaimsParser
{
    private static readonly string[] ManagementClaimTypeNames =
    [
        ManagementClaimTypes.OperatorId,
        ManagementClaimTypes.OperatorSource,
        ManagementClaimTypes.OperatorDisplayName,
        ManagementClaimTypes.Permission,
    ];

    /// <summary>
    /// Gets a shared parser instance.
    /// </summary>
    public static ManagementClaimsParser Instance { get; } = new();

    /// <inheritdoc />
    public ManagementClaimsParseResult Parse(ClaimsPrincipal? principal)
    {
        if (principal is null)
        {
            return ManagementClaimsParseResult.Unauthenticated();
        }

        var authenticatedIdentities = principal.Identities
            .Where(identity => identity.IsAuthenticated)
            .ToArray();
        if (authenticatedIdentities.Length == 0)
        {
            return ManagementClaimsParseResult.Unauthenticated();
        }

        if (authenticatedIdentities.Length > 1)
        {
            return ManagementClaimsParseResult.Invalid(
                WellKnownManagementIdentityErrorCodes.IdentityAmbiguous);
        }

        var identity = authenticatedIdentities[0];
        if (principal.Identities.Any(other =>
                !ReferenceEquals(other, identity) && CarriesManagementClaim(other)))
        {
            return ManagementClaimsParseResult.Invalid(
                WellKnownManagementIdentityErrorCodes.ClaimsSplit);
        }

        return ParseIdentity(identity);
    }

    private static ManagementClaimsParseResult ParseIdentity(ClaimsIdentity identity)
    {
        if (!TrySingle(identity, ManagementClaimTypes.OperatorId, out var rawOperatorId))
        {
            return ManagementClaimsParseResult.Invalid(
                WellKnownManagementIdentityErrorCodes.OperatorIdInvalid);
        }

        if (!TrySingle(identity, ManagementClaimTypes.OperatorSource, out var rawSource) ||
            !ManagementAuditOperatorSource.TryParse(rawSource, out var source) ||
            source is null ||
            !string.Equals(rawSource, source.Value, StringComparison.Ordinal))
        {
            // Parsing then comparing ordinally accepts only an already normalized wire value without
            // changing the general normalization contract of the audit operator source parser.
            return ManagementClaimsParseResult.Invalid(
                WellKnownManagementIdentityErrorCodes.OperatorSourceInvalid);
        }

        var displayNameClaims = ClaimValues(identity, ManagementClaimTypes.OperatorDisplayName);
        if (displayNameClaims.Count > 1)
        {
            return ManagementClaimsParseResult.Invalid(
                WellKnownManagementIdentityErrorCodes.DisplayNameInvalid);
        }

        var rawDisplayName = displayNameClaims.Count == 1 ? displayNameClaims[0] : null;

        var permissionClaims = ClaimValues(identity, ManagementClaimTypes.Permission);
        if (permissionClaims.Count == 0)
        {
            return ManagementClaimsParseResult.Invalid(
                WellKnownManagementIdentityErrorCodes.PermissionInvalid);
        }

        var granted = new HashSet<ManagementPermission>();
        foreach (var permissionClaim in permissionClaims)
        {
            if (!ManagementPermissions.TryParse(permissionClaim, out var permission) ||
                !granted.Add(permission))
            {
                return ManagementClaimsParseResult.Invalid(
                    WellKnownManagementIdentityErrorCodes.PermissionInvalid);
            }
        }

        ManagementAuditOperator auditOperator;
        try
        {
            auditOperator = ManagementAuditOperator.Create(source, rawOperatorId, rawDisplayName);
        }
        catch (ManagementAuditException exception)
        {
            return ManagementClaimsParseResult.Invalid(
                exception.ErrorCode == "audit.operator_display_name_invalid"
                    ? WellKnownManagementIdentityErrorCodes.DisplayNameInvalid
                    : WellKnownManagementIdentityErrorCodes.OperatorIdInvalid);
        }

        // Sanitizing then comparing ordinally keeps the existing audit cleaning rules as the single
        // validation authority while still rejecting every value that is not already in its cleaned
        // wire form. Without this, whitespace and control-character variants of one operator would
        // alias onto the same identity.
        if (!string.Equals(rawOperatorId, auditOperator.OperatorId, StringComparison.Ordinal))
        {
            return ManagementClaimsParseResult.Invalid(
                WellKnownManagementIdentityErrorCodes.OperatorIdInvalid);
        }

        if (!string.Equals(rawDisplayName, auditOperator.DisplayName, StringComparison.Ordinal))
        {
            return ManagementClaimsParseResult.Invalid(
                WellKnownManagementIdentityErrorCodes.DisplayNameInvalid);
        }

        return ManagementClaimsParseResult.Parsed(ManagementIdentity.FromValidated(
            auditOperator,
            ManagementPermissions.All.Where(granted.Contains).ToArray()));
    }

    private static bool CarriesManagementClaim(ClaimsIdentity identity) =>
        identity.Claims.Any(claim => ManagementClaimTypeNames.Contains(claim.Type, StringComparer.Ordinal));

    private static List<string> ClaimValues(ClaimsIdentity identity, string claimType) =>
        identity.Claims
            .Where(claim => string.Equals(claim.Type, claimType, StringComparison.Ordinal))
            .Select(claim => claim.Value)
            .ToList();

    private static bool TrySingle(ClaimsIdentity identity, string claimType, out string value)
    {
        var values = ClaimValues(identity, claimType);
        if (values.Count != 1)
        {
            value = string.Empty;
            return false;
        }

        value = values[0];
        return true;
    }
}
