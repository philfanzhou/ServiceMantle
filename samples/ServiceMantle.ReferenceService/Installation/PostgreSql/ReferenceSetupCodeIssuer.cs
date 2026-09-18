using System.Globalization;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Installation;
using ServiceMantle.Persistence.EntityFrameworkCore;
using ServiceMantle.ReferenceService.Database.PostgreSql;

namespace ServiceMantle.ReferenceService.Installation.PostgreSql;

/// <summary>
/// Issues the sample's one-time Setup Code at startup, exactly when the PostgreSQL gate has
/// resolved the host to <c>PendingSetup</c>, and prints it to the console seam.
/// </summary>
/// <remarks>
/// <para>
/// The issuer runs after the gate's own hosted step - the registration order fixes that - so its
/// view of the startup phase is the gate's published database fact, not a guess. It touches the
/// store only in that one pending state: a completed, failed, or cancelled gate leaves the store
/// untouched and prints nothing.
/// </para>
/// <para>
/// The plaintext reaches standard output through <see cref="ReferenceSetupCodeOutput"/> only.
/// A second instance that loses the issuance race, a restart while a code is still outstanding,
/// and a failing store all print fixed hints without the plaintext and without rotating; rotation
/// belongs to the explicit <c>--rotate-setup-code</c> command alone. A caller cancellation
/// propagates with its own token and stops the startup.
/// </para>
/// </remarks>
public sealed class ReferenceSetupCodeIssuer(
    ReferencePostgreSqlStartupHostedService gate,
    IDbContextFactory<ReferencePostgreSqlDbContext> factory,
    ServiceId serviceId,
    ReferenceSetupCodeOutput output) : IHostedLifecycleService
{
    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        if (gate.Result is not { IsReady: true, ServiceStartupPhase: ServiceStartupPhase.PendingSetup })
        {
            return;
        }

        SetupCodeIssueResult issued;
        try
        {
            await using var context = await factory.CreateDbContextAsync(cancellationToken)
                .ConfigureAwait(false);
            issued = await new EfCoreServiceSetupCodeStore<ReferencePostgreSqlDbContext>(context)
                .CreateAsync(serviceId, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // The store could not be reached or refused the issuance: the host keeps running in
            // PendingSetup and the operator recovers with the rotation command. No provider text
            // and no plaintext reach the console.
            await output.Error.WriteLineAsync(IssuanceFailedHint).ConfigureAwait(false);
            return;
        }

        switch (issued.ErrorCode)
        {
            case null:
                // The one place the plaintext is ever written.
                await output.Out.WriteLineAsync("one-time setup code:").ConfigureAwait(false);
                await output.Out.WriteLineAsync(issued.SetupCode!.Reveal()).ConfigureAwait(false);
                await output.Out.WriteLineAsync(
                    "expires at " + issued.ExpiresAtUtc!.Value.ToUniversalTime()
                        .ToString("O", CultureInfo.InvariantCulture)).ConfigureAwait(false);
                await output.Out.WriteLineAsync(RotationHint).ConfigureAwait(false);
                break;
            case WellKnownSetupCodeErrorCodes.AlreadyExists:
            case WellKnownSetupCodeErrorCodes.ConcurrencyConflict:
                // This instance lost the race or a code is still outstanding; nothing is rotated
                // and no plaintext is printed.
                await output.Out.WriteLineAsync(AlreadyIssuedHint).ConfigureAwait(false);
                break;
            case WellKnownSetupCodeErrorCodes.InstallationCompleted:
                break;
            default:
                await output.Error.WriteLineAsync(IssuanceFailedHint).ConfigureAwait(false);
                break;
        }
    }

    /// <summary>The fixed hint every rotation path prints.</summary>
    public const string RotationHint = "for a new code later run with --rotate-setup-code";

    /// <summary>The fixed hint a restart prints while a code is still outstanding.</summary>
    public const string AlreadyIssuedHint =
        "a setup code is already issued; " + RotationHint;

    /// <summary>The fixed hint a failed issuance or rotation prints.</summary>
    public const string IssuanceFailedHint =
        "the setup code could not be issued; " + RotationHint;

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
