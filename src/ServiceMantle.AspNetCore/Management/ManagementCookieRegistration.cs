using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceMantle.AspNetCore.Management;

internal sealed record ManagementCookieRegistration(
    bool HttpOnly,
    CookieSecurePolicy SecurePolicy,
    SameSiteMode SameSite,
    bool IsEssential,
    TimeSpan ExpireTimeSpan,
    bool SlidingExpiration,
    bool AllowInsecureTransport,
    string CookieName,
    string ApplicationName)
{
    internal static ManagementCookieRegistration Create(
        ManagementCookieOptions options,
        ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceId);

        return new ManagementCookieRegistration(
            options.HttpOnly,
            options.SecurePolicy,
            options.SameSite,
            options.IsEssential,
            options.ExpireTimeSpan,
            options.SlidingExpiration,
            options.AllowInsecureTransport,
            GetCookieName(options.AllowInsecureTransport, options.SecurePolicy),
            $"ServiceMantle.Management:{serviceId.Value}");
    }

    internal static string GetCookieName(bool allowInsecureTransport, CookieSecurePolicy securePolicy) =>
        allowInsecureTransport && securePolicy == CookieSecurePolicy.SameAsRequest
            ? ManagementSessionDefaults.InsecureTransportCookieName
            : ManagementSessionDefaults.CookieName;
}

internal sealed class ManagementCookieStartupValidator(
    IEnumerable<ManagementCookieRegistration> registrations,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
    IOptions<DataProtectionOptions> dataProtectionOptions,
    ILogger<ManagementCookieStartupValidator> logger) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        var configured = registrations.ToArray();
        var registration = configured[0];
        if (configured.Any(candidate => candidate != registration))
        {
            throw new InvalidOperationException(
                "Conflicting ServiceMantle management cookie settings are registered.");
        }

        ValidateRegistration(registration);
        var effectiveOptions = cookieOptions.Get(ManagementSessionDefaults.AuthenticationScheme);
        ValidateEffectiveOptions(effectiveOptions, registration);

        if (!string.Equals(
                dataProtectionOptions.Value.ApplicationDiscriminator,
                registration.ApplicationName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The ServiceMantle management Data Protection application name was overridden.");
        }

        if (registration.AllowInsecureTransport &&
            effectiveOptions.Cookie.SecurePolicy != CookieSecurePolicy.Always)
        {
            logger.LogWarning(
                "The ServiceMantle management cookie allows insecure transport. Management " +
                "credentials and session ids will be transmitted in plaintext on HTTP requests.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateRegistration(ManagementCookieRegistration options)
    {
        if (!options.HttpOnly)
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie must remain HttpOnly.");
        }

        if (options.SecurePolicy == CookieSecurePolicy.None)
        {
            throw new InvalidOperationException(options.AllowInsecureTransport
                ? "The ServiceMantle management cookie cannot use SecurePolicy.None even when " +
                  "insecure transport is allowed; use SameAsRequest instead."
                : "The ServiceMantle management cookie must always require secure transport.");
        }

        if (options.SecurePolicy != CookieSecurePolicy.Always && !options.AllowInsecureTransport)
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie must always require secure transport.");
        }

        if (options.SameSite == SameSiteMode.None)
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie cannot use SameSite=None.");
        }

        if (!options.IsEssential)
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie must remain essential.");
        }

        if (options.ExpireTimeSpan <= TimeSpan.Zero ||
            options.ExpireTimeSpan > TimeSpan.FromHours(
                ManagementSessionDefaults.MaximumExpireTimeSpanHours))
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie lifetime is outside the permitted range.");
        }
    }

    private static void ValidateEffectiveOptions(
        CookieAuthenticationOptions options,
        ManagementCookieRegistration registration)
    {
        var effective = new ManagementCookieRegistration(
            options.Cookie.HttpOnly,
            options.Cookie.SecurePolicy,
            options.Cookie.SameSite,
            options.Cookie.IsEssential,
            options.ExpireTimeSpan,
            options.SlidingExpiration,
            registration.AllowInsecureTransport,
            ManagementCookieRegistration.GetCookieName(
                registration.AllowInsecureTransport,
                options.Cookie.SecurePolicy),
            string.Empty);
        ValidateRegistration(effective);

        if (!string.Equals(
                options.Cookie.Name,
                effective.CookieName,
                StringComparison.Ordinal) ||
            !string.Equals(options.Cookie.Path, "/", StringComparison.Ordinal) ||
            options.Cookie.Domain is not null)
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie host scope was overridden.");
        }
    }
}
