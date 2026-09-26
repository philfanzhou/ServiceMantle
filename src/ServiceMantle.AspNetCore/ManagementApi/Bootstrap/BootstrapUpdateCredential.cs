using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.AspNetCore.ManagementApi.Entries;

namespace ServiceMantle.AspNetCore.ManagementApi.Bootstrap;

/// <summary>One <c>AddServiceMantleBootstrapManagement</c> call's Bearer scheme choice.</summary>
internal sealed record BootstrapUpdateCredentialRegistration(string? BearerScheme);

/// <summary>
/// The fixed names and the single resolution point of the opt-in Bearer credential of the
/// Bootstrap update entry.
/// </summary>
/// <remarks>
/// The entry authenticates through one policy scheme whose selector forwards every operation to
/// the configured Bearer scheme when an <c>Authorization</c> header is present and to the fixed
/// management cookie otherwise. One scheme decides per request, so the authorization evaluation
/// never merges two principals and an invalid Bearer credential never falls back to the cookie.
/// </remarks>
internal sealed class BootstrapUpdateCredential(
    IEnumerable<BootstrapUpdateCredentialRegistration> registrations)
{
    /// <summary>The policy scheme that selects the Bearer scheme or the management cookie.</summary>
    internal const string SelectorScheme = "ServiceMantle.ManagementBootstrapUpdateCredential";

    /// <summary>The session policy of the update entry when the Bearer credential is enabled.</summary>
    internal const string SessionPolicyName = "ServiceMantle.ManagementBootstrapUpdateSession";

    private readonly BootstrapUpdateCredentialRegistration[] registrations = [.. registrations];

    /// <summary>
    /// True when any registration names a Bearer scheme. The mapping uses this to pin the entry to
    /// the selector; a conflicting set of registrations is refused when the host starts.
    /// </summary>
    internal bool IsEnabled => registrations.Any(registration => registration.BearerScheme is not null);

    /// <summary>
    /// The configured Bearer scheme, or <see langword="null"/> when the option is not enabled.
    /// </summary>
    /// <exception cref="InvalidOperationException">The registrations disagree.</exception>
    internal string? GetBearerScheme()
    {
        if (registrations.Length == 0)
        {
            return null;
        }

        var scheme = registrations[0].BearerScheme;
        if (registrations.Any(registration =>
                !string.Equals(registration.BearerScheme, scheme, StringComparison.Ordinal)))
        {
            throw BootstrapMapping.InvalidUpdateCredential();
        }

        return scheme;
    }

    /// <summary>
    /// Selects the scheme of one request: any <c>Authorization</c> header, however malformed,
    /// empty, or repeated, belongs to the Bearer scheme.
    /// </summary>
    internal static string Select(HttpContext context, string bearerScheme) =>
        context.Request.Headers.ContainsKey(HeaderNames.Authorization)
            ? bearerScheme
            : ManagementSessionDefaults.AuthenticationScheme;

    /// <summary>
    /// Resolves the update entry's selected credential before rate limiting reads the operator.
    /// Authorization later uses the same scheme and its per-request authentication result.
    /// </summary>
    internal static async Task AuthenticateBeforeRateLimitingAsync(HttpContext context, RequestDelegate next)
    {
        if (context.GetEndpoint()?.Metadata.GetMetadata<ManagementEntryMetadata>()?.Kind ==
            ManagementEntryKind.BootstrapUpdate)
        {
            var result = await context.AuthenticateAsync(SelectorScheme).ConfigureAwait(false);
            // A failed selected credential must not retain the default cookie's quota or identity.
            context.User = result.Succeeded && result.Principal is not null
                ? result.Principal
                : new ClaimsPrincipal(new ClaimsIdentity());
        }

        await next(context).ConfigureAwait(false);
    }

    /// <summary>
    /// Refuses an unusable configuration before the host serves anything. Every failure carries
    /// the same fixed message, which names no scheme or other configured value.
    /// </summary>
    internal async Task ValidateAsync(IAuthenticationSchemeProvider schemes)
    {
        var bearerScheme = GetBearerScheme();
        if (bearerScheme is null)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(bearerScheme) ||
            string.Equals(bearerScheme, ManagementSessionDefaults.AuthenticationScheme, StringComparison.Ordinal) ||
            string.Equals(bearerScheme, SelectorScheme, StringComparison.Ordinal))
        {
            throw BootstrapMapping.InvalidUpdateCredential();
        }

        var bearer = await schemes.GetSchemeAsync(bearerScheme).ConfigureAwait(false);
        if (bearer is null ||
            typeof(PolicySchemeHandler).IsAssignableFrom(bearer.HandlerType) ||
            await schemes.GetSchemeAsync(SelectorScheme).ConfigureAwait(false) is null ||
            await schemes.GetSchemeAsync(ManagementSessionDefaults.AuthenticationScheme)
                .ConfigureAwait(false) is null)
        {
            throw BootstrapMapping.InvalidUpdateCredential();
        }
    }
}
