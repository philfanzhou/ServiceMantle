using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.Management;
using ServiceMantle.AspNetCore.ManagementApi.Bootstrap;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.Bootstrap;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the anonymous Bootstrap creation and the administrator Bootstrap update entries.
/// </summary>
public static class ServiceMantleBootstrapEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>POST</c> and <c>PUT {versionedRoot}/bootstrap</c> with the shared management entry
    /// baseline and the ServiceMantle-owned Bootstrap handlers.
    /// </summary>
    /// <param name="endpoints">The application the ServiceMantle pipeline was composed on.</param>
    /// <returns>The same endpoint route builder.</returns>
    /// <remarks>
    /// <para>
    /// Both entries are opt-in and are mapped beside the protected group returned by
    /// <c>MapServiceMantleManagementApiV1</c>, never inside it. With the default root the full path
    /// is <c>/management/v1/bootstrap</c>; a custom versioned root moves it. The shared baseline
    /// admits the creation only in <c>BootstrapConfiguration</c> and the update only when the
    /// service is ready, applies the named rate-limit policies and the security response headers,
    /// and requires exactly one <c>X-ServiceMantle-Request: 1</c> header on both.
    /// </para>
    /// <para>
    /// The creation entry is anonymous and is authorized by exactly one
    /// <c>X-ServiceMantle-Bootstrap-Credential</c> header holding the one-time credential, which is
    /// consumed before the file is written. The update entry adds the fixed management cookie
    /// session policy to the administrator policy, so its conclusion comes from this host's own
    /// cookie; it never reads the creation credential. Mapping this group more than once, or
    /// without a registered <c>IBootstrapCredentialStore</c> or the fixed cookie scheme, fails
    /// before the host starts.
    /// </para>
    /// <para>
    /// A committed creation answers <c>201 {"restartRequired":true}</c> and a committed update
    /// <c>200 {"restartRequired":true}</c>; both only after the manager published the file, which
    /// also latches the process-local restart requirement the status entry reports. An unusable
    /// request shape answers the fixed management <c>400</c>, an unusable credential
    /// <c>401 {"errorCode":"management.bootstrap.credential_invalid"}</c>, a target the store
    /// refused to create or replace the fixed management <c>409</c>, and every store, validator, or
    /// internal failure <c>503 {"errorCode":"management.bootstrap.unavailable"}</c>. Caller
    /// cancellation propagates its original token.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The credential store or the shared management entry capability is not registered, or the
    /// entries are mapped more than once.
    /// </exception>
    public static IEndpointRouteBuilder MapServiceMantleBootstrap(this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var registration = endpoints.ServiceProvider
            .GetService<BootstrapManagementRegistration>()
            ?? throw BootstrapMapping.MissingCapability();

        // The store is looked up as a registration rather than resolved, so a consumer may register
        // it with any lifetime and nothing is constructed while the routes are being mapped.
        if (endpoints.ServiceProvider.GetService<IServiceProviderIsService>()
            is not { } isService ||
            !isService.IsService(typeof(IBootstrapCredentialStore)))
        {
            throw BootstrapMapping.MissingCredentialStore();
        }

        BootstrapMapping.RecordMap(endpoints.ServiceProvider);
        registration.RecordMap();

        endpoints.MapServiceMantleManagementEntry(
            ManagementEntryKind.BootstrapCreate,
            (HttpContext context) => BootstrapHandlers.CreateAsync(context));
        endpoints
            .MapServiceMantleManagementEntry(
                ManagementEntryKind.BootstrapUpdate,
                (HttpContext context) => BootstrapHandlers.UpdateAsync(context))
            // The shared administrator policy is authentication-method agnostic by design. This
            // mapping adds the session policy, whose scheme list is the fixed management cookie, so
            // rewriting a running instance's Bootstrap file cannot be authorized by an external
            // default scheme. No shared entry file, general policy, or cookie implementation is
            // changed by adding it here.
            .RequireAuthorization(ManagementAuthorizationDefaults.SessionPolicyName);
        return endpoints;
    }
}
