using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

/// <summary>Records whether this host actually mapped the Bootstrap management entries.</summary>
internal sealed class ServiceMantleBootstrapManagementRegistration
{
    private int mapped;

    internal bool Mapped => Volatile.Read(ref mapped) != 0;

    internal void RecordMap() => Volatile.Write(ref mapped, 1);
}

/// <summary>
/// Rejects a host that mapped the Bootstrap update entry without the fixed management cookie
/// authentication scheme its authorization resolves through.
/// </summary>
/// <remarks>
/// A host that did not map this group gains no prerequisite of its own. The check names no
/// scheme value, operator, credential, or configuration value.
/// </remarks>
internal sealed class ServiceMantleBootstrapManagementStartupValidator(
    ServiceMantleBootstrapManagementRegistration registration,
    IServiceProvider services) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!registration.Mapped)
        {
            return;
        }

        var schemes = services.GetService<IAuthenticationSchemeProvider>()
            ?? throw ServiceMantleBootstrapMapping.MissingCapability();
        if (await schemes
                .GetSchemeAsync(ServiceMantleManagementSessionDefaults.AuthenticationScheme)
                .ConfigureAwait(false) is null)
        {
            throw ServiceMantleBootstrapMapping.MissingCapability();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
