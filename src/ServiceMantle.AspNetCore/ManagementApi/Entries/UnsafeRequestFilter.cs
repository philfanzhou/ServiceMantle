using Microsoft.AspNetCore.Http;

namespace ServiceMantle.AspNetCore.ManagementApi.Entries;

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
internal sealed class UnsafeRequestFilter : IEndpointFilter
{
    internal static readonly UnsafeRequestFilter Instance = new();

    private UnsafeRequestFilter()
    {
    }

    public ValueTask<object?> InvokeAsync(
        EndpointFilterInvocationContext context,
        EndpointFilterDelegate next)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(next);

        var request = context.HttpContext.Request;
        if (!ManagementEntryDefaults.IsUnsafeMethod(request.Method))
        {
            return next(context);
        }

        var values = request.Headers[ManagementEntryDefaults.UnsafeRequestHeaderName];
        if (values.Count != 1 || !string.Equals(
                values[0],
                ManagementEntryDefaults.UnsafeRequestHeaderValue,
                StringComparison.Ordinal))
        {
            return ValueTask.FromResult<object?>(ManagementApiResults.InvalidRequest());
        }

        return next(context);
    }
}
