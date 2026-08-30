using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.CookiePolicy;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;

namespace ServiceMantle.Management;

internal sealed record ServiceMantleManagementCookieRegistration(
    bool HttpOnly,
    CookieSecurePolicy SecurePolicy,
    SameSiteMode SameSite,
    bool IsEssential,
    TimeSpan ExpireTimeSpan,
    bool SlidingExpiration,
    string ApplicationName)
{
    internal static ServiceMantleManagementCookieRegistration Create(
        ServiceMantleManagementCookieOptions options,
        ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(serviceId);

        return new ServiceMantleManagementCookieRegistration(
            options.HttpOnly,
            options.SecurePolicy,
            options.SameSite,
            options.IsEssential,
            options.ExpireTimeSpan,
            options.SlidingExpiration,
            $"ServiceMantle.Management:{serviceId.Value}");
    }
}

internal sealed class ServiceMantleManagementCookieStartupValidator(
    IEnumerable<ServiceMantleManagementCookieRegistration> registrations,
    IOptionsMonitor<CookieAuthenticationOptions> cookieOptions,
    IOptions<DataProtectionOptions> dataProtectionOptions) : IHostedService
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
        ValidateEffectiveOptions(
            cookieOptions.Get(ServiceMantleManagementSessionDefaults.AuthenticationScheme));

        if (!string.Equals(
                dataProtectionOptions.Value.ApplicationDiscriminator,
                registration.ApplicationName,
                StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The ServiceMantle management Data Protection application name was overridden.");
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static void ValidateRegistration(ServiceMantleManagementCookieRegistration options)
    {
        if (!options.HttpOnly)
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie must remain HttpOnly.");
        }

        if (options.SecurePolicy != CookieSecurePolicy.Always)
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
                ServiceMantleManagementSessionDefaults.MaximumExpireTimeSpanHours))
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie lifetime is outside the permitted range.");
        }
    }

    private static void ValidateEffectiveOptions(CookieAuthenticationOptions options)
    {
        var effective = new ServiceMantleManagementCookieRegistration(
            options.Cookie.HttpOnly,
            options.Cookie.SecurePolicy,
            options.Cookie.SameSite,
            options.Cookie.IsEssential,
            options.ExpireTimeSpan,
            options.SlidingExpiration,
            string.Empty);
        ValidateRegistration(effective);

        if (!string.Equals(
                options.Cookie.Name,
                ServiceMantleManagementSessionDefaults.CookieName,
                StringComparison.Ordinal) ||
            !string.Equals(options.Cookie.Path, "/", StringComparison.Ordinal) ||
            options.Cookie.Domain is not null)
        {
            throw new InvalidOperationException(
                "The ServiceMantle management cookie host scope was overridden.");
        }
    }
}
