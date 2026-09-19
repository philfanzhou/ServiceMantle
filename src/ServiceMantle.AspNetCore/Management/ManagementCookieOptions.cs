using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.Http;

namespace ServiceMantle.AspNetCore.Management;

/// <summary>
/// Configures the security and lifetime of the ServiceMantle management session cookie.
/// </summary>
/// <remarks>
/// Unsafe values are rejected when the host starts. The cookie name and authentication scheme are
/// fixed by <see cref="ManagementSessionDefaults"/> and cannot be configured here.
/// </remarks>
public sealed class ManagementCookieOptions
{
    /// <summary>
    /// Gets or sets whether the cookie is inaccessible to client-side script.
    /// </summary>
    public bool HttpOnly { get; set; } = true;

    /// <summary>
    /// Gets or sets the secure transport policy.
    /// </summary>
    /// <remarks>
    /// The default requires <see cref="CookieSecurePolicy.Always"/>. <see cref="CookieSecurePolicy.SameAsRequest"/>
    /// is accepted only when <see cref="AllowInsecureTransport"/> is explicitly enabled;
    /// <see cref="CookieSecurePolicy.None"/> is rejected in every configuration.
    /// </remarks>
    public CookieSecurePolicy SecurePolicy { get; set; } = CookieSecurePolicy.Always;

    /// <summary>
    /// Gets or sets whether the management session cookie may be issued without the Secure
    /// attribute on plain-HTTP intranet deployments.
    /// </summary>
    /// <remarks>
    /// The default is <see langword="false"/>: the effective secure transport policy must be
    /// <see cref="CookieSecurePolicy.Always"/> and anything else fails when the host starts.
    /// When set to <see langword="true"/> together with
    /// <see cref="SecurePolicy"/> set to <see cref="CookieSecurePolicy.SameAsRequest"/>, startup
    /// accepts the relaxed policy, logs a warning, and uses the fixed cookie name without the
    /// <c>__Host-</c> prefix, which browsers require to be paired with the Secure attribute.
    /// This explicitly accepts that management credentials and session ids are transmitted in
    /// plaintext on HTTP requests; network isolation and access control remain the caller's
    /// responsibility. All other cookie gates still apply unchanged.
    /// </remarks>
    public bool AllowInsecureTransport { get; set; }

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
