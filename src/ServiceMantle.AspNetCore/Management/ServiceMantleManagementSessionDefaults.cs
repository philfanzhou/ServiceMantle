namespace ServiceMantle.Management;

/// <summary>
/// Fixed names, lifetimes, and safe API result codes for ServiceMantle management sessions.
/// </summary>
public static class ServiceMantleManagementSessionDefaults
{
    /// <summary>
    /// The authentication scheme used by the ServiceMantle management cookie.
    /// </summary>
    public const string AuthenticationScheme = "ServiceMantle.ManagementCookie";

    /// <summary>
    /// The fixed host-scoped cookie name. It deliberately carries no service or deployment identity.
    /// </summary>
    public const string CookieName = "__Host-ServiceMantle.Management";

    /// <summary>
    /// The default absolute ticket lifetime, in hours.
    /// </summary>
    public const int DefaultExpireTimeSpanHours = 8;

    /// <summary>
    /// The maximum permitted absolute ticket lifetime, in hours.
    /// </summary>
    public const int MaximumExpireTimeSpanHours = 24;

    /// <summary>
    /// Whether sliding renewal is enabled by default.
    /// </summary>
    public const bool DefaultSlidingExpiration = true;

    /// <summary>
    /// The error code returned when no management session was presented.
    /// </summary>
    public const string UnauthenticatedErrorCode = "management.session.unauthenticated";

    /// <summary>
    /// The error code returned when a presented management session cannot be accepted.
    /// </summary>
    public const string ExpiredErrorCode = "management.session.expired";

    /// <summary>
    /// The error code returned when the current management identity lacks permission.
    /// </summary>
    public const string ForbiddenErrorCode = "management.session.forbidden";
}
