using System.Collections.ObjectModel;
using System.Security.Claims;
using ServiceMantle.Audit;

namespace ServiceMantle.Management;

/// <summary>
/// An immutable, validated management operator identity: the operator identifier, the identity
/// source, an optional display name, and a de-duplicated closed permission set.
/// </summary>
public sealed class ManagementIdentity
{
    private readonly ManagementAuditOperator auditOperator;

    private ManagementIdentity(
        ManagementAuditOperator auditOperator,
        ReadOnlyCollection<ManagementPermission> permissions)
    {
        this.auditOperator = auditOperator;
        Permissions = permissions;
    }

    /// <summary>
    /// Gets the operator identifier.
    /// </summary>
    public string OperatorId => auditOperator.OperatorId!;

    /// <summary>
    /// Gets the identity source the operator was asserted through.
    /// </summary>
    public ManagementAuditOperatorSource Source => auditOperator.Source;

    /// <summary>
    /// Gets the operator display name, or <see langword="null"/> when none was asserted.
    /// </summary>
    public string? DisplayName => auditOperator.DisplayName;

    /// <summary>
    /// Gets the de-duplicated permissions in the fixed <see cref="ManagementPermission"/> order.
    /// </summary>
    /// <remarks>
    /// The set is wrapped in a <see cref="ReadOnlyCollection{T}"/>, so a caller cannot cast it back
    /// to <see cref="ManagementPermission"/><c>[]</c> and change what this identity grants after it
    /// was validated. Mutating the sequence passed to <see cref="Create"/> afterwards has no effect
    /// either, because the ordered set is materialized during construction.
    /// </remarks>
    public IReadOnlyList<ManagementPermission> Permissions { get; }

    /// <summary>
    /// Creates a validated management identity.
    /// </summary>
    /// <param name="source">The identity source the operator was asserted through.</param>
    /// <param name="operatorId">The operator identifier.</param>
    /// <param name="permissions">At least one defined permission; duplicates are collapsed.</param>
    /// <param name="displayName">An optional operator display name.</param>
    /// <exception cref="ArgumentException">
    /// The operator identifier is blank, the permission set is empty, or a permission is not a
    /// defined value.
    /// </exception>
    /// <exception cref="ManagementAuditException">
    /// The operator identifier or display name violates the audit operator content rules.
    /// </exception>
    public static ManagementIdentity Create(
        ManagementAuditOperatorSource source,
        string operatorId,
        IEnumerable<ManagementPermission> permissions,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(operatorId);
        ArgumentNullException.ThrowIfNull(permissions);

        var orderedPermissions = OrderPermissions(permissions, nameof(permissions));
        var auditOperator = ManagementAuditOperator.Create(source, operatorId, displayName);
        if (auditOperator.OperatorId is null)
        {
            throw new ArgumentException(
                "A management operator identifier must not be blank.",
                nameof(operatorId));
        }

        if (displayName is not null && auditOperator.DisplayName is null)
        {
            throw new ArgumentException(
                "A management operator display name must not be blank when supplied.",
                nameof(displayName));
        }

        return new ManagementIdentity(auditOperator, orderedPermissions);
    }

    /// <summary>
    /// Creates an identity from material the claims parser has already validated, so that neither the
    /// operator identifier nor the display name is cleaned or redacted a second time.
    /// </summary>
    internal static ManagementIdentity FromValidated(
        ManagementAuditOperator auditOperator,
        HashSet<ManagementPermission> grantedPermissions) =>
        new(auditOperator, OrderGranted(grantedPermissions));

    /// <summary>
    /// Determines whether this identity carries a permission.
    /// </summary>
    public bool HasPermission(ManagementPermission permission) =>
        Permissions.Contains(permission);

    /// <summary>
    /// Projects this identity onto the existing audit operator model without a second operator model.
    /// </summary>
    public ManagementAuditOperator ToAuditOperator() => auditOperator;

    /// <summary>
    /// Creates the standard ServiceMantle <see cref="ClaimsIdentity"/> for this identity.
    /// </summary>
    /// <remarks>
    /// The identity uses <see cref="ManagementIdentityDefaults.AuthenticationType"/> and is therefore
    /// authenticated. Every claim uses <see cref="ClaimValueTypes.String"/> and
    /// <see cref="ClaimsIdentity.DefaultIssuer"/>, and no role or other implicit claim is emitted.
    /// </remarks>
    public ClaimsIdentity ToClaimsIdentity()
    {
        var claims = new List<Claim>(Permissions.Count + 2)
        {
            CreateClaim(ManagementClaimTypes.OperatorId, OperatorId),
            CreateClaim(ManagementClaimTypes.OperatorSource, Source.Value),
        };

        if (DisplayName is not null)
        {
            claims.Add(CreateClaim(ManagementClaimTypes.OperatorDisplayName, DisplayName));
        }

        foreach (var permission in Permissions)
        {
            claims.Add(CreateClaim(
                ManagementClaimTypes.Permission,
                ManagementPermissions.ToWireValue(permission)));
        }

        return new ClaimsIdentity(claims, ManagementIdentityDefaults.AuthenticationType);
    }

    /// <summary>
    /// Creates a <see cref="ClaimsPrincipal"/> holding exactly one standard ServiceMantle identity.
    /// </summary>
    public ClaimsPrincipal ToClaimsPrincipal() => new(ToClaimsIdentity());

    /// <summary>
    /// Returns a safe projection that never includes the display name.
    /// </summary>
    public override string ToString() =>
        $"ManagementIdentity(Source={Source}, OperatorId={OperatorId}, Permissions={Permissions.Count})";

    private static Claim CreateClaim(string type, string value) => new(
        type,
        value,
        ClaimValueTypes.String,
        ClaimsIdentity.DefaultIssuer,
        ClaimsIdentity.DefaultIssuer);

    private static ReadOnlyCollection<ManagementPermission> OrderPermissions(
        IEnumerable<ManagementPermission> permissions,
        string parameterName)
    {
        var granted = new HashSet<ManagementPermission>();
        foreach (var permission in permissions)
        {
            if (!ManagementPermissions.IsDefined(permission))
            {
                throw new ArgumentException(
                    "A management permission is not a defined value.",
                    parameterName);
            }

            granted.Add(permission);
        }

        if (granted.Count == 0)
        {
            throw new ArgumentException(
                "A management identity requires at least one permission.",
                parameterName);
        }

        return OrderGranted(granted);
    }

    private static ReadOnlyCollection<ManagementPermission> OrderGranted(
        HashSet<ManagementPermission> granted) =>
        ManagementPermissions.All.Where(granted.Contains).ToArray().AsReadOnly();
}
