using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using ServiceMantle.AspNetCore.ManagementApi.Entries;
using ServiceMantle.AspNetCore.ManagementApi.Setup;

namespace Microsoft.AspNetCore.Builder;

/// <summary>
/// Maps the anonymous Setup status and completion entries of the ServiceMantle management surface.
/// </summary>
public static class ServiceMantleSetupEndpointRouteBuilderExtensions
{
    /// <summary>
    /// Maps <c>GET</c>, <c>HEAD</c>, and <c>POST {versionedRoot}/setup</c> with the shared
    /// management entry baseline and the ServiceMantle-owned Setup handlers.
    /// </summary>
    /// <param name="endpoints">The application the ServiceMantle pipeline was composed on.</param>
    /// <param name="executor">
    /// The consumer-owned transaction boundary. It returns Committed only after commit completes.
    /// </param>
    /// <returns>The same endpoint route builder.</returns>
    /// <remarks>
    /// The entries are opt-in and are mapped beside the protected group returned by
    /// <c>MapServiceMantleManagementApiV1</c>, never inside it. With the default root the full path
    /// is <c>/management/v1/setup</c>; a custom versioned root moves it. The shared baseline admits
    /// them only in <c>PendingSetup</c> or <c>Completed</c> with migration Succeeded and the
    /// database Reachable, keeps them anonymous under the Setup rate-limit policy with the security
    /// response headers and the correlation contract, and requires exactly one
    /// <c>X-ServiceMantle-Request: 1</c> header on the <c>POST</c>.
    /// <para>
    /// The read entry projects exactly <c>{"status":"pending"}</c> or <c>{"status":"completed"}</c>
    /// from the existing installation authority and exposes no Setup Code generation, digest,
    /// issuance or expiry time, operator, contributor, or stored value. <c>HEAD</c> answers the same
    /// status and headers with no body.
    /// </para>
    /// <para>
    /// The completion entry answers the fixed management conflict for an already completed
    /// installation without parsing the supplied code, so a replay is a stable boundary. Otherwise
    /// it accepts only <c>application/json</c> with an optional UTF-8 charset, no query string and
    /// no content encoding, a raw body of at most 4 KiB, JSON depth at most 4, and exactly one
    /// case-sensitive top-level <c>code</c> string holding the existing 32-character Base64URL Setup
    /// Code, never trimmed. Every unusable shape answers the fixed management 400 without echoing
    /// the request. A committed completion answers <c>204</c> with an empty body; a rejected code
    /// answers <c>401 {"errorCode":"management.setup.credential_invalid"}</c> with no sub-reason; a
    /// concurrent completion or version conflict answers the fixed management 409; and a store,
    /// orchestrator, save, commit, cleanup, or internal timeout failure answers
    /// <c>503 {"errorCode":"management.setup.unavailable"}</c>. Caller cancellation propagates its
    /// original token.
    /// </para>
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// The executor is missing, the shared management entry capability or management API v1
    /// capability is not registered, or the entries are mapped more than once.
    /// </exception>
    public static IEndpointRouteBuilder MapServiceMantleSetup(
        this IEndpointRouteBuilder endpoints,
        SetupExecutor? executor)
    {
        ArgumentNullException.ThrowIfNull(endpoints);
        if (executor is null)
        {
            throw SetupMapping.MissingExecutor();
        }

        SetupMapping.RecordMap(endpoints.ServiceProvider);
        endpoints.MapServiceMantleManagementEntry(
            ManagementEntryKind.SetupStatus,
            (HttpContext context) => SetupHandlers.StatusAsync(context));
        endpoints.MapServiceMantleManagementEntry(
            ManagementEntryKind.SetupComplete,
            (HttpContext context) => SetupHandlers.CompleteAsync(context, executor));
        return endpoints;
    }
}
