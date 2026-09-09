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
/// <para>
/// Neither handler introduces a second installation authority, issues or rotates a Setup Code, or
/// writes anything of its own. ServiceMantle owns no wall-clock budget here: a Setup commit is the
/// consumer's transaction, so an internal timeout owned by the store or the executor is mapped to
/// the fixed unavailable result rather than imposed by this endpoint.
/// </para>
/// <para>
/// The caller's own cancellation outranks everything a boundary produced. At each observation point
/// - the store read, the body read and parse, and the executor - the request's abort is checked
/// before the outcome is classified, so a request that was aborted while an internal failure or an
/// unrelated internal cancellation happened is answered with the caller's cancellation rather than
/// with a fixed result.
/// </para>
/// </remarks>
internal static class ServiceMantleSetupHandlers
{
    /// <summary>Projects only whether the installation is pending or completed.</summary>
    internal static async Task<IResult> StatusAsync(HttpContext context)
    {
        ObserveCallerCancellation(context);
        var state = await TryReadAsync(context).ConfigureAwait(false);
        ObserveCallerCancellation(context);
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
        ObserveCallerCancellation(context);

        // A completed installation is a stable replay boundary: it answers the fixed conflict
        // without reading, parsing, or validating the supplied code at all.
        var state = await TryReadAsync(context).ConfigureAwait(false);
        ObserveCallerCancellation(context);
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
        catch
        {
            ObserveCallerCancellation(context);
            return ServiceMantleSetupResult.Unavailable;
        }

        ObserveCallerCancellation(context);
        if (code is null)
        {
            // An unusable HTTP shape and an unusable code shape are one fixed rejection each, and
            // neither echoes a byte of the request.
            return ServiceMantleManagementApiResults.InvalidRequest();
        }

        ServiceMantleSetupCompletionResult? result;
        try
        {
            result = await executor(context, code, context.RequestAborted).ConfigureAwait(false);
        }
        catch
        {
            // An executor failure, an internal timeout, and an unrelated internal cancellation are
            // one safe outcome. The reason is never projected. The executor is not called a second
            // time and nothing is retried, rolled back, or committed here.
            ObserveCallerCancellation(context);
            return ServiceMantleSetupResult.Unavailable;
        }

        ObserveCallerCancellation(context);
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
        catch
        {
            ObserveCallerCancellation(context);
            return null;
        }
    }

    /// <summary>
    /// Observes the caller's own cancellation, in preference to whatever a boundary produced.
    /// </summary>
    /// <remarks>
    /// The thrown exception is created here and carries exactly
    /// <see cref="HttpContext.RequestAborted"/>. It is never the exception a boundary raised, so an
    /// internal cancellation carrying another token is not propagated as if it were the caller's,
    /// and no internal message, inner exception, supplied code, or connection detail can reach the
    /// caller through it. When the request was not aborted, the boundary keeps its existing fixed
    /// result: an internal cancellation, an internal timeout, and an ordinary failure all stay the
    /// same safe outcome.
    /// </remarks>
    private static void ObserveCallerCancellation(HttpContext context)
    {
        var token = context.RequestAborted;
        if (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }
}
