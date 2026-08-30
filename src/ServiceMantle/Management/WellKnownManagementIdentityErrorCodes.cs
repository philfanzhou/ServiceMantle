namespace ServiceMantle.Management;

/// <summary>
/// Well-known safe error codes for management identity resolution.
/// </summary>
/// <remarks>
/// Every code is a stable classification only. Codes never carry claim values, tokens, cookies,
/// credentials, display name text, or provider-internal error information.
/// </remarks>
public static class WellKnownManagementIdentityErrorCodes
{
    /// <summary>
    /// The identity provider or an upstream identity system failed.
    /// </summary>
    public const string ProviderFailed = "management_identity.provider_failed";

    /// <summary>
    /// The principal does not contain exactly one authenticated identity.
    /// </summary>
    public const string IdentityAmbiguous = "management_identity.identity_ambiguous";

    /// <summary>
    /// ServiceMantle operator or permission claims are spread across more than one identity.
    /// </summary>
    public const string ClaimsSplit = "management_identity.claims_split";

    /// <summary>
    /// The principal carries ServiceMantle operator or permission claims, but no identity holding
    /// them is authenticated.
    /// </summary>
    public const string ClaimsUnauthenticated = "management_identity.claims_unauthenticated";

    /// <summary>
    /// The operator identifier claim is missing, duplicated, or not acceptable.
    /// </summary>
    public const string OperatorIdInvalid = "management_identity.operator_id_invalid";

    /// <summary>
    /// The operator source claim is missing, duplicated, or not an already normalized wire value.
    /// </summary>
    public const string OperatorSourceInvalid = "management_identity.operator_source_invalid";

    /// <summary>
    /// The operator display name claim is duplicated or not acceptable.
    /// </summary>
    public const string DisplayNameInvalid = "management_identity.display_name_invalid";

    /// <summary>
    /// The permission claims are missing, duplicated, or contain an unknown value.
    /// </summary>
    public const string PermissionInvalid = "management_identity.permission_invalid";

    private static readonly HashSet<string> DefinedCodes = new(StringComparer.Ordinal)
    {
        ProviderFailed,
        IdentityAmbiguous,
        ClaimsSplit,
        ClaimsUnauthenticated,
        OperatorIdInvalid,
        OperatorSourceInvalid,
        DisplayNameInvalid,
        PermissionInvalid,
    };

    /// <summary>
    /// Determines whether a value is one of the codes declared by this type.
    /// </summary>
    /// <remarks>
    /// The comparison is exact and ordinal. The ServiceMantle-owned rejection results - the claims
    /// parser result and the current-operator result - accept only these codes, so no caller-shaped
    /// string can reach a public <c>ErrorCode</c> or <c>ToString()</c>.
    /// </remarks>
    public static bool IsDefined(string? errorCode) =>
        errorCode is not null && DefinedCodes.Contains(errorCode);
}
