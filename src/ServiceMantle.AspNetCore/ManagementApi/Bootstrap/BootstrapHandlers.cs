using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore.ManagementApi.Status;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.AspNetCore.ManagementApi.Bootstrap;

/// <summary>
/// Answers the two Bootstrap management entries from the existing instance-local file manager and
/// the consumer-registered one-time credential store.
/// </summary>
/// <remarks>
/// <para>
/// The handlers introduce no file format, no validation rule, and no authorization capability of
/// their own. They order the existing ones: parse strictly, consume the one-time credential before
/// the creation is attempted, project the manager's finite failures onto fixed responses, and latch
/// the restart requirement as soon as the manager confirms the file was published.
/// </para>
/// <para>
/// The caller's own cancellation outranks everything a boundary produced. At each observation point
/// the request's abort is checked before the outcome is classified, and the exception that is thrown
/// carries exactly <see cref="HttpContext.RequestAborted"/> - never an internal token, message, or
/// inner exception.
/// </para>
/// <para>
/// Consumption and publication are two files and not one transaction. A validator rejection, an I/O
/// failure, a cancellation, or a lost response after a successful consumption leaves the credential
/// consumed; a lost response after a successful publication leaves the file written and the latch
/// set. Neither is restored here.
/// </para>
/// </remarks>
internal static class BootstrapHandlers
{
    /// <summary>Creates the instance-local Bootstrap file from an anonymous, credentialed request.</summary>
    internal static async Task<IResult> CreateAsync(HttpContext context)
    {
        ObserveCallerCancellation(context);

        var parsed = await ParseAsync(context, requireBoth: true).ConfigureAwait(false);
        if (parsed is null)
        {
            return ManagementApiResults.InvalidRequest();
        }

        // The credential's shape is checked before anything is consumed, so a malformed or repeated
        // header never reaches the store.
        if (!TryReadCredential(context, out var candidate))
        {
            return BootstrapResult.CredentialInvalid;
        }

        var store = context.RequestServices.GetService<IBootstrapCredentialStore>();
        if (store is null)
        {
            return BootstrapResult.Unavailable;
        }

        BootstrapCredentialConsumptionResult? consumption;
        try
        {
            consumption = await store.ConsumeAsync(candidate, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch
        {
            ObserveCallerCancellation(context);
            return BootstrapResult.Unavailable;
        }

        ObserveCallerCancellation(context);
        if (consumption is null)
        {
            return BootstrapResult.Unavailable;
        }

        if (!consumption.IsConsumed)
        {
            // Only the store's own finite classification decides this: an invalid candidate is a
            // rejection, and a storage failure is not.
            return string.Equals(
                consumption.ErrorCode,
                WellKnownBootstrapCredentialErrorCodes.Invalid,
                StringComparison.Ordinal)
                ? BootstrapResult.CredentialInvalid
                : BootstrapResult.Unavailable;
        }

        // From here the credential is gone. Nothing below restores it.
        var manager = context.RequestServices.GetRequiredService<BootstrapConfigurationManager>();
        return await PublishAsync(
            context,
            () => manager.CreateAsync(
                new BootstrapCreateRequest(parsed.Database!, parsed.MasterKey!),
                context.RequestAborted),
            BootstrapResult.Created).ConfigureAwait(false);
    }

    /// <summary>Replaces the instance-local Bootstrap file for an authenticated administrator.</summary>
    internal static async Task<IResult> UpdateAsync(HttpContext context)
    {
        ObserveCallerCancellation(context);

        var parsed = await ParseAsync(context, requireBoth: false).ConfigureAwait(false);
        if (parsed is null)
        {
            return ManagementApiResults.InvalidRequest();
        }

        // The update entry never reads the creation credential and is never authorized by one.
        var manager = context.RequestServices.GetRequiredService<BootstrapConfigurationManager>();
        return await PublishAsync(
            context,
            () => manager.UpdateAsync(
                new BootstrapUpdateRequest(parsed.Database, parsed.MasterKey),
                context.RequestAborted),
            BootstrapResult.Updated).ConfigureAwait(false);
    }

    private static async ValueTask<BootstrapRequest?> ParseAsync(
        HttpContext context,
        bool requireBoth)
    {
        BootstrapRequest? parsed;
        try
        {
            parsed = await BootstrapRequestParser
                .ParseAsync(context, requireBoth)
                .ConfigureAwait(false);
        }
        catch
        {
            ObserveCallerCancellation(context);
            return null;
        }

        ObserveCallerCancellation(context);
        return parsed;
    }

    /// <summary>
    /// Runs one manager write and projects its finite failures. The restart latch is set as soon as
    /// the manager returns, because at that point the file has been published.
    /// </summary>
    private static async ValueTask<IResult> PublishAsync(
        HttpContext context,
        Func<ValueTask<BootstrapChangeResult>> publish,
        IResult success)
    {
        try
        {
            _ = await publish().ConfigureAwait(false);
        }
        catch (BootstrapManagementException exception)
        {
            ObserveCallerCancellation(context);
            // Only the code is compared. It is never written to the response, a message, or a log.
            return BootstrapMapping.IsUnavailableCode(exception.ErrorCode)
                ? BootstrapResult.Unavailable
                : ManagementApiResults.InvalidRequest();
        }
        catch (BootstrapException exception)
        {
            ObserveCallerCancellation(context);
            // The store's own classification decides this; the message, the path, and the inner
            // exception are never parsed, and existence is never guessed at separately.
            return exception.FailureKind is BootstrapFileFailureKind.TargetAlreadyExists
                or BootstrapFileFailureKind.TargetMissing
                ? ManagementApiResults.Conflict()
                : BootstrapResult.Unavailable;
        }
        catch
        {
            ObserveCallerCancellation(context);
            // An internal failure, an internal timeout, and an unrelated internal cancellation are
            // one safe outcome. Nothing about the file is claimed.
            return BootstrapResult.Unavailable;
        }

        // The file is published. The latch is set before the request's later cancellation or a
        // failed response is considered, so this process never claims the file is unchanged.
        context.RequestServices.GetRequiredService<BootstrapRestartLatch>().Latch();
        ObserveCallerCancellation(context);
        return success;
    }

    /// <summary>
    /// Reads the one credential candidate the creation entry accepts, exactly as it arrived.
    /// </summary>
    /// <remarks>
    /// Exactly one header value is accepted, and its 43-character Base64URL shape is the core rule.
    /// A missing, repeated, or comma-combined value is refused here, before the store is called.
    /// </remarks>
    private static bool TryReadCredential(HttpContext context, out string? candidate)
    {
        candidate = null;
        var values = context.Request.Headers[BootstrapMapping.CredentialHeaderName];
        if (values.Count != 1)
        {
            return false;
        }

        var value = values[0];
        if (!BootstrapCredential.TryParse(value, out _))
        {
            return false;
        }

        candidate = value;
        return true;
    }

    /// <summary>
    /// Observes the caller's own cancellation, in preference to whatever a boundary produced.
    /// </summary>
    private static void ObserveCallerCancellation(HttpContext context)
    {
        var token = context.RequestAborted;
        if (token.IsCancellationRequested)
        {
            throw new OperationCanceledException(token);
        }
    }
}
