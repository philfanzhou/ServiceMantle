using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;

internal static class SettingUpdateHandlers
{
    internal static async Task<IResult> UpdateAsync(
        HttpContext context,
        SettingUpdateExecutor executor)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        try
        {
            var resolver = context.RequestServices.GetRequiredService<IManagementCurrentOperatorResolver>();
            var resolution = resolver.Resolve(context.User);

            // Cancellation observed after the resolver settles outranks its classification: a
            // resolved, rejected, or internally cancelled resolution never delivers Forbid, and the
            // request body is never read.
            context.RequestAborted.ThrowIfCancellationRequested();
            if (resolution?.Status != ManagementCurrentOperatorStatus.Resolved
                || resolution.Operator is null)
            {
                return Results.Forbid();
            }

            var command = await SettingUpdateRequestParser
                .ParseAsync(context, resolution.Operator)
                .ConfigureAwait(false);

            // Cancellation observed after the body parser settles outranks its classification: a
            // produced, null, or internally cancelled parse never delivers the 400, and the executor
            // is never invoked.
            context.RequestAborted.ThrowIfCancellationRequested();
            if (command is null)
            {
                return ManagementApiResults.InvalidRequest();
            }

            var result = await executor(context, command, context.RequestAborted).ConfigureAwait(false);
            context.RequestAborted.ThrowIfCancellationRequested();
            return Map(result);
        }
        catch (Exception)
        {
            context.RequestAborted.ThrowIfCancellationRequested();
            return SettingUpdateResult.Unavailable;
        }
    }

    private static IResult Map(ServiceSettingUpdateResult? result) => result?.Status switch
    {
        ServiceSettingUpdateStatus.Applied when result.Version is > 0 =>
            SettingUpdateResult.Applied(result.Version.Value),
        ServiceSettingUpdateStatus.ValidationFailed => ManagementApiResults.InvalidRequest(),
        ServiceSettingUpdateStatus.VersionConflict or ServiceSettingUpdateStatus.VersionExhausted =>
            ManagementApiResults.Conflict(),
        _ => SettingUpdateResult.Unavailable
    };
}
