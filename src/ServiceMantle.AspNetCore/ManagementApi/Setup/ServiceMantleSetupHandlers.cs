using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Installation;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Answers the two Setup entries from the current installation authority and one consumer-owned
/// completion transaction.
/// </summary>
/// <remarks>
/// Neither handler introduces a second installation authority, issues or rotates a Setup Code, or
/// writes anything of its own. ServiceMantle owns no wall-clock budget here: a Setup commit is the
/// consumer's transaction, so an internal timeout owned by the store or the executor is mapped to
/// the fixed unavailable result rather than imposed by this endpoint.
/// </remarks>
internal static class ServiceMantleSetupHandlers
{
    /// <summary>Projects only whether the installation is pending or completed.</summary>
    internal static async Task<IResult> StatusAsync(HttpContext context)
    {
        context.RequestAborted.ThrowIfCancellationRequested();
        var state = await TryReadAsync(context).ConfigureAwait(false);
        context.RequestAborted.ThrowIfCancellationRequested();
        return state switch
        {
            null => ServiceMantleSetupResult.Unavailable,
            { IsCompleted: true } => ServiceMantleSetupResult.Completed,
            _ => ServiceMantleSetupResult.Pending,
        };
    }

    /// <summary>
    /// Completes the installation through the consumer transaction executor, and answers success
    /// only once that transaction has committed.
    /// </summary>
    internal static async Task<IResult> CompleteAsync(
        HttpContext context,
        ServiceMantleSetupExecutor executor)
    {
        context.RequestAborted.ThrowIfCancellationRequested();

        // A completed installation is a stable replay boundary: it answers the fixed conflict
        // without reading, parsing, or validating the supplied code at all.
        var state = await TryReadAsync(context).ConfigureAwait(false);
        context.RequestAborted.ThrowIfCancellationRequested();
        if (state is null)
        {
            return ServiceMantleSetupResult.Unavailable;
        }

        if (state.IsCompleted)
        {
            return ServiceMantleManagementApiResults.Conflict();
        }

        SetupCode? code;
        try
        {
            code = await ServiceMantleSetupRequestParser.ParseAsync(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return ServiceMantleSetupResult.Unavailable;
        }

        if (code is null)
        {
            // An unusable HTTP shape and an unusable code shape are one fixed rejection each, and
            // neither echoes a byte of the request.
            return ServiceMantleManagementApiResults.InvalidRequest();
        }

        context.RequestAborted.ThrowIfCancellationRequested();
        ServiceMantleSetupCompletionResult? result;
        try
        {
            result = await executor(context, code, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // An executor failure, an internal timeout, and an unrelated internal cancellation are
            // one safe outcome. The reason is never projected.
            return ServiceMantleSetupResult.Unavailable;
        }

        context.RequestAborted.ThrowIfCancellationRequested();
        return result?.Status switch
        {
            ServiceMantleSetupCompletionStatus.Committed => ServiceMantleSetupResult.NoContent,
            ServiceMantleSetupCompletionStatus.CredentialInvalid => ServiceMantleSetupResult.CredentialInvalid,
            ServiceMantleSetupCompletionStatus.Conflict => ServiceMantleManagementApiResults.Conflict(),
            ServiceMantleSetupCompletionStatus.ValidationFailed => ServiceMantleManagementApiResults.InvalidRequest(),
            // Unavailable, a null result, and an undefined status are the same safe outcome.
            _ => ServiceMantleSetupResult.Unavailable,
        };
    }

    /// <summary>
    /// Reads the installation authority once, or returns null for every safe failure. Caller
    /// cancellation keeps its original token and is never converted into a fixed result.
    /// </summary>
    private static async ValueTask<ServiceInstallationState?> TryReadAsync(HttpContext context)
    {
        try
        {
            var store = context.RequestServices.GetService<IServiceInstallationStore>();
            var serviceId = context.RequestServices.GetRequiredService<ServiceId>();
            return store is null
                ? null
                : await store.FindAsync(serviceId, context.RequestAborted).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }
}
