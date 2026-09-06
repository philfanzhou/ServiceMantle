using Microsoft.AspNetCore.Http;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Rejects an unsafe management entry request that does not carry exactly one
/// <c>X-ServiceMantle-Request: 1</c> header, before the entry handler runs.
/// </summary>
/// <remarks>
/// The guard is applied only to the unsafe methods of the shared entries. Safe methods pass through
/// untouched, so the guard itself has no side effect on a <c>GET</c> or <c>HEAD</c>. A missing,
/// empty, repeated, comma-combined, or different value produces the fixed management invalid-request
/// result, which carries no request value.
/// </remarks>
internal sealed class ServiceMantleUnsafeRequestFilter : IEndpointFilter
{
    internal static readonly ServiceMantleUnsafeRequestFilter Instance = new();

    private ServiceMantleUnsafeRequestFilter()
    {
    }

    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.HttpContext.Request;
        if (!ServiceMantleManagementEntryDefaults.IsUnsafeMethod(request.Method))
        {
            return next(context);
        }

        var values = request.Headers[ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderName];
        if (values.Count != 1 || !string.Equals(
                values[0],
                ServiceMantleManagementEntryDefaults.UnsafeRequestHeaderValue,
                StringComparison.Ordinal))
        {
            return ValueTask.FromResult<object?>(ServiceMantleManagementApiResults.InvalidRequest());
        }

        return next(context);
    }
}
