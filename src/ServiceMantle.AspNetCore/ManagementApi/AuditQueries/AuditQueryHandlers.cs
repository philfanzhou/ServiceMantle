using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Audit;

namespace ServiceMantle.AspNetCore.ManagementApi.AuditQueries;

/// <summary>
/// Adapts the bounded management audit query contract to its closed HTTP response.
/// </summary>
internal static class AuditQueryHandlers
{
    internal static async Task<IResult> QueryAsync(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        if (!AuditQueryMapping.TryParse(context, out var query))
        {
            return ManagementApiResults.InvalidRequest();
        }

        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var service = context.RequestServices.GetRequiredService<IManagementAuditQueryService>();
            var result = await service.QueryAsync(query!, context.RequestAborted).ConfigureAwait(false);
            context.RequestAborted.ThrowIfCancellationRequested();
            return AuditQueryResult.Success(result);
        }
        catch (Exception) when (context.RequestAborted.IsCancellationRequested)
        {
            // A query dependency - including the scoped service resolution - that settles after
            // the request aborted, whether it answered, failed validation, or cancelled
            // internally, does not get to deliver its ordinary response mapping, and its own
            // exception is never propagated either.
            throw CancelledByCaller(context.RequestAborted);
        }
        catch (ManagementAuditException exception) when (
            exception.ErrorCode.StartsWith("audit.query_", StringComparison.Ordinal))
        {
            return ManagementApiResults.InvalidRequest();
        }
        catch (Exception)
        {
            return AuditQueryResult.Unavailable;
        }
    }

    /// <summary>
    /// Builds the caller's own cancellation result. It carries the request token and nothing
    /// else: no dependency exception, no audit error code, and no consumer text.
    /// </summary>
    private static OperationCanceledException CancelledByCaller(CancellationToken cancellationToken) =>
        new("The management audit query request was cancelled by the caller.", cancellationToken);
}
