using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle.AspNetCore;
using ServiceMantle.Management;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
/// Registers the opt-in shared ServiceMantle management entry convention.
/// </summary>
public static class ServiceMantleManagementEntryBuilderExtensions
{
    /// <summary>
    /// Adds the shared management entry convention on top of the registered management API v1 root.
    /// </summary>
    /// <param name="builder">The ServiceMantle builder.</param>
    /// <returns>The same builder.</returns>
    /// <remarks>
    /// The registration adds no endpoint of its own: call <c>MapServiceMantleManagementEntry</c> for
    /// each entry the consuming service actually serves. It registers the management authorization
    /// contract and the <c>ServiceMantle.ManagementSession</c> policy, which requires a legitimate
    /// operator authenticated through the fixed management cookie scheme but no permission. It
    /// deliberately registers no cookie handler, identity provider, rate-limit policy, security
    /// header capability, or business handler; the consuming service still adds
    /// <c>AddServiceMantleManagementApiV1</c>, <c>AddManagementCookieAuthentication</c>,
    /// <c>AddSecurityResponseHeaders</c>, <c>AddSensitiveHeaders</c>, and <c>AddRateLimiting</c>,
    /// and composes the pipeline with <c>UseServiceMantlePipeline</c>. Repeated registration is
    /// idempotent; a missing capability fails before the host starts.
    /// </remarks>
    public static ServiceMantleBuilder AddServiceMantleManagementEntries(this ServiceMantleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(descriptor =>
                descriptor.ServiceType == typeof(ServiceMantleManagementEntryState)))
        {
            return builder;
        }

        builder.Services.AddServiceMantleManagementAuthorization();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IAuthorizationHandler,
            ManagementSessionAuthorizationHandler>());
        builder.Services.AddAuthorization(options => options.AddPolicy(
            ManagementAuthorizationDefaults.SessionPolicyName,
            new AuthorizationPolicyBuilder()
                .AddAuthenticationSchemes(ServiceMantleManagementSessionDefaults.AuthenticationScheme)
                .RequireAuthenticatedUser()
                .AddRequirements(new ManagementSessionRequirement())
                .Build()));
        builder.Services.AddSingleton<ServiceMantleManagementEntryState>();
        builder.Services.TryAddEnumerable(ServiceDescriptor.Singleton<
            IHostedService,
            ServiceMantleManagementEntryStartupValidator>());
        return builder;
    }
}
