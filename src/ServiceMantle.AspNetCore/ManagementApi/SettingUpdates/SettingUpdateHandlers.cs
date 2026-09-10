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
            if (resolution?.Status != ManagementCurrentOperatorStatus.Resolved
                || resolution.Operator is null)
            {
                return Results.Forbid();
            }

            var command = await SettingUpdateRequestParser
                .ParseAsync(context, resolution.Operator)
                .ConfigureAwait(false);
            if (command is null)
            {
                return ManagementApiResults.InvalidRequest();
            }

            context.RequestAborted.ThrowIfCancellationRequested();
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
