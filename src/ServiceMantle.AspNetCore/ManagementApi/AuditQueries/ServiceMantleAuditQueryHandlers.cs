using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Audit;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Adapts the bounded management audit query contract to its closed HTTP response.
/// </summary>
internal static class ServiceMantleAuditQueryHandlers
{
    internal static async Task<IResult> QueryAsync(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        if (!ServiceMantleAuditQueryMapping.TryParse(context, out var query))
        {
            return ServiceMantleManagementApiResults.InvalidRequest();
        }

        try
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            var service = context.RequestServices.GetRequiredService<IManagementAuditQueryService>();
            var result = await service.QueryAsync(query!, context.RequestAborted).ConfigureAwait(false);
            context.RequestAborted.ThrowIfCancellationRequested();
            return ServiceMantleAuditQueryResult.Success(result);
        }
        catch (ManagementAuditException exception) when (
            exception.ErrorCode.StartsWith("audit.query_", StringComparison.Ordinal))
        {
            return ServiceMantleManagementApiResults.InvalidRequest();
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ServiceMantleAuditQueryResult.Unavailable;
        }
    }
}
