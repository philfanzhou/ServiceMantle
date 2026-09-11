using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.ManagementApi.Bootstrap;
using ServiceMantle.AspNetCore.ManagementApi.Status;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the opt-in ServiceMantle Bootstrap management entries.
/// </summary>
public static class ServiceMantleBootstrapManagementBuilderExtensions
{
    /// <summary>
    /// Adds the Bootstrap creation and update entries' own prerequisites on top of the shared
    /// management entry convention.
    /// </summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The registration adds no endpoint: call <c>MapServiceMantleBootstrap</c> to serve them. It
    /// reuses the singletons <c>AddServiceMantle</c> already registered - the Bootstrap file store,
    /// its manager, and the provider registry - and shares the one process-local restart latch with
    /// the installation status entry. It adds the one-time credential header to the denied header
    /// names so the value can never reach a ServiceMantle log line.
    /// <para>
    /// It deliberately registers no <c>IBootstrapCredentialStore</c>: a consuming service registers
    /// the store explicitly, ServiceMantle never provisions a credential on its behalf, and no HTTP
    /// request configures where the credential is stored. Mapping without a registered store fails
    /// before the host starts. The consuming service still adds
    /// <c>AddServiceMantleManagementApiV1</c>, <c>AddManagementCookieAuthentication</c>,
    /// <c>AddSecurityResponseHeaders</c>, and <c>AddRateLimiting</c>, and composes the pipeline with
    /// <c>UseServiceMantlePipeline</c>. Repeated registration is idempotent.
    /// </para>
    /// </remarks>
    public static ServiceMantleBuilder AddServiceMantleBootstrapManagement(
        this ServiceMantleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.AddServiceMantleManagementEntries();
        // The registry unions every registration with the built-in denied names, so this adds one
        // name without touching the built-in list or another registration's.
        builder.AddSensitiveHeaders(options =>
            options.DeniedHeaderNames = [BootstrapMapping.CredentialHeaderName]);
        builder.Services.TryAddSingleton<BootstrapRestartLatch>();
        builder.Services.TryAddSingleton<BootstrapManagementRegistration>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            BootstrapManagementStartupValidator>());
        return builder;
    }
}
