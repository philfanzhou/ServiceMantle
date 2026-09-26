namespace ServiceMantle.AspNetCore.ManagementApi.Bootstrap;

/// <summary>
/// Configures the opt-in ServiceMantle Bootstrap management entries.
/// </summary>
public sealed class BootstrapManagementOptions
{
    /// <summary>
    /// Gets or sets the name of an already registered management Bearer authentication scheme the
    /// Bootstrap update entry also accepts. The default is <see langword="null"/>: the update entry
    /// accepts only the fixed management cookie, exactly as without this option.
    /// </summary>
    /// <remarks>
    /// <para>
    /// When set, a request to the Bootstrap update entry that carries an <c>Authorization</c> header
    /// in any form (including an empty or repeated one) is authenticated, challenged, and forbidden
    /// by this scheme alone; a request without one keeps the fixed management cookie. The two are
    /// never combined, so an invalid Bearer credential sent together with a valid cookie is
    /// rejected by this scheme and never falls back to the cookie. No other management entry
    /// accepts this scheme.
    /// </para>
    /// <para>
    /// The scheme must issue legitimate management identities (for example principals built with
    /// <c>ManagementIdentity</c>) and nothing else; the administrator permission, the session
    /// requirement, the phase gate, the unsafe-request header, the rate limit, and the audit stay
    /// exactly as for the cookie. The host fails to start when the name is blank, is not a
    /// registered scheme, is the fixed management cookie scheme, or names a policy scheme that
    /// forwards to another scheme. ServiceMantle does not enforce HTTPS: over plain HTTP the Bearer
    /// credential travels in clear text, so deploy the entry behind HTTPS.
    /// </para>
    /// </remarks>
    public string? UpdateBearerAuthenticationScheme { get; set; }

    /// <summary>Returns only non-sensitive configuration metadata.</summary>
    public override string ToString() =>
        $"BootstrapManagementOptions(UpdateBearerEnabled={UpdateBearerAuthenticationScheme is not null})";
}
