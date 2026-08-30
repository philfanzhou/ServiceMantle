using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Http;

namespace ServiceMantle.Management;

/// <summary>
/// Configures the security and lifetime of the ServiceMantle management session cookie.
/// </summary>
/// <remarks>
/// Unsafe values are rejected when the host starts. The cookie name and authentication scheme are
/// fixed by <see cref="ServiceMantleManagementSessionDefaults"/> and cannot be configured here.
/// </remarks>
public sealed class ServiceMantleManagementCookieOptions
{
    /// <summary>
    /// Gets or sets whether the cookie is inaccessible to client-side script.
    /// </summary>
    public bool HttpOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets the secure transport policy.
    /// </summary>
    public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.Always;

    /// <summary>
    /// Gets or sets the cross-site cookie policy.
    /// </summary>
    public SameSiteMode SameSite { get; set; } = SameSiteMode.Strict;

    /// <summary>
    /// Gets or sets whether the cookie is essential for consent policy purposes.
    /// </summary>
    public bool IsEssential { get; set; } = true;

    /// <summary>
    /// Gets or sets the absolute ticket lifetime.
    /// </summary>
    public TimeSpan ExpireTimeSpan { get; set; } = TimeSpan.FromHours(
        ServiceMantleManagementSessionDefaults.DefaultExpireTimeSpanHours);

    /// <summary>
    /// Gets or sets whether a valid ticket is renewed after more than half its lifetime has elapsed.
    /// </summary>
    public bool SlidingExpiration { get; set; } =
        ServiceMantleManagementSessionDefaults.DefaultSlidingExpiration;
}
