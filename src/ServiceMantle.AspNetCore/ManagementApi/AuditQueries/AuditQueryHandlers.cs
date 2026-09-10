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
        catch (ManagementAuditException exception) when (
            exception.ErrorCode.StartsWith("audit.query_", StringComparison.Ordinal))
        {
            return ManagementApiResults.InvalidRequest();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return AuditQueryResult.Unavailable;
        }
    }
}
