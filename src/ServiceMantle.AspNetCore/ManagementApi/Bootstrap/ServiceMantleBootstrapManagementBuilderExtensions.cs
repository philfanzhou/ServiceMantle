using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Management;
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
        this ServiceMantleBuilder builder) =>
        AddServiceMantleBootstrapManagement(builder, static _ => { });

    /// <summary>
    /// Adds the Bootstrap creation and update entries' own prerequisites and applies the opt-in
    /// options, for example the management Bearer scheme the update entry also accepts.
    /// </summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <param name="configure">Configures the Bootstrap management options.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// Everything the parameterless overload registers is registered here too, and the
    /// parameterless overload is equivalent to leaving every option unset. When
    /// <see cref="BootstrapManagementOptions.UpdateBearerAuthenticationScheme"/> is set, the update
    /// entry authenticates through the fixed policy scheme
    /// <c>ServiceMantle.ManagementBootstrapUpdateCredential</c>, which forwards a request carrying
    /// an <c>Authorization</c> header to that scheme and every other request to the fixed
    /// management cookie, and it is authorized by the fixed
    /// <c>ServiceMantle.ManagementBootstrapUpdateSession</c> policy in place of
    /// <c>ServiceMantle.ManagementSession</c>. No other entry or policy changes. Registrations with
    /// equivalent options are idempotent; registrations that disagree, and a scheme that is blank,
    /// unregistered, the fixed management cookie, or a forwarding policy scheme, fail when the host
    /// starts with a fixed message that names no configured value.
    /// </remarks>
    public static ServiceMantleBuilder AddServiceMantleBootstrapManagement(
        this ServiceMantleBuilder builder,
        Action<BootstrapManagementOptions> configure)
    {
        ArgumentNullException.ThrowIfNull(builder);
        ArgumentNullException.ThrowIfNull(configure);

        var options = new BootstrapManagementOptions();
        configure(options);

        builder.AddServiceMantleManagementEntries();
        // The registry unions every registration with the built-in denied names, so this adds one
        // name without touching the built-in list or another registration's.
        builder.AddSensitiveHeaders(options =>
            options.DeniedHeaderNames = [BootstrapMapping.CredentialHeaderName]);
        builder.Services.TryAddSingleton<BootstrapRestartLatch>();
        builder.Services.TryAddSingleton<BootstrapManagementRegistration>();
        builder.Services.TryAddSingleton<BootstrapUpdateCredential>();
        builder.Services.AddSingleton(
            new BootstrapUpdateCredentialRegistration(options.UpdateBearerAuthenticationScheme));
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            BootstrapManagementStartupValidator>());

        if (options.UpdateBearerAuthenticationScheme is not null &&
            !builder.Services.Any(descriptor =>
                descriptor.ServiceType == typeof(BootstrapUpdateCredentialSelectorMarker)))
        {
            builder.Services.AddSingleton<BootstrapUpdateCredentialSelectorMarker>();
            builder.Services.AddAuthentication().AddPolicyScheme(
                BootstrapUpdateCredential.SelectorScheme,
                displayName: null,
                selector => selector.ForwardDefaultSelector = static context =>
                    BootstrapUpdateCredential.Select(
                        context,
                        context.RequestServices.GetRequiredService<BootstrapUpdateCredential>()
                            .GetBearerScheme()!));
            builder.Services.AddAuthorization(authorization => authorization.AddPolicy(
                BootstrapUpdateCredential.SessionPolicyName,
                new AuthorizationPolicyBuilder()
                    .AddAuthenticationSchemes(BootstrapUpdateCredential.SelectorScheme)
                    .RequireAuthenticatedUser()
                    .AddRequirements(new ManagementSessionRequirement())
                    .Build()));
        }

        return builder;
    }

    /// <summary>Marks the one-time registration of the update entry's selector scheme and policy.</summary>
    private sealed class BootstrapUpdateCredentialSelectorMarker;
}
