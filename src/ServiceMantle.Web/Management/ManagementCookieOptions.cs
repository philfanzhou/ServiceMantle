using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Http;

namespace ServiceMantle.Web.Management;

/// <summary>
/// Configures the security and lifetime of the ServiceMantle management session cookie.
/// </summary>
/// <remarks>
/// The cookie name and authentication scheme are fixed by <see cref="ManagementSessionDefaults"/>
/// and cannot be configured here. Transport security follows the request scheme by default and is
/// the deployment's decision; the remaining unsafe values are rejected when the host starts.
/// </remarks>
public sealed class ManagementCookieOptions
{
    /// <summary>
    /// Gets or sets whether the cookie is inaccessible to client-side script.
    /// </summary>
    public bool HttpOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets the secure transport policy. The default follows the request scheme, so the
    /// management session works over both plain HTTP and HTTPS deployments.
    /// </summary>
    public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.SameAsRequest;

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
        ManagementSessionDefaults.DefaultExpireTimeSpanHours);

    /// <summary>
    /// Gets or sets whether a valid ticket is renewed after more than half its lifetime has elapsed.
    /// </summary>
    public bool SlidingExpiration { get; set; } =
        ManagementSessionDefaults.DefaultSlidingExpiration;
}
