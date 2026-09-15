using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using ServiceMantle;
using ServiceMantle.Installation;
using ServiceMantle.Migration;

namespace ServiceMantle.ReferenceService.Database.PostgreSql;

/// <summary>
/// The consumer-owned composite executor that initialises an empty PostgreSQL target and its
/// initial pending installation row in one transaction.
/// </summary>
/// <remarks>
/// <para>
/// The schema-only <see cref="ReferencePostgreSqlMigrationExecutor"/> this class composes with
/// leaves a gap: a fresh target migrated by it and the initial <c>PendingSetup</c> installation
/// row written afterwards are two separate commits, and a crash between them produces a database
/// indistinguishable from an old workspace-only one. This executor closes that gap. When its own
/// <see cref="InspectAsync"/> observed <see cref="MigrationObservationState.Empty"/> in this
/// orchestration scope, <see cref="ExecuteAsync"/> applies every migration this build knows and
/// creates the initial pending installation row inside one PostgreSQL transaction, committing
/// once: any failure, caller cancellation, or connection loss before that commit leaves no
/// table, no history, and no installation row behind.
/// </para>
/// <para>
/// <see cref="InspectAsync"/> fully delegates to the schema executor; the observation matrix,
/// read-only behaviour, and cancellation checkpoints are unchanged. A target observed as
/// <see cref="MigrationObservationState.PendingMigration"/> is delegated to the schema
/// executor's own execution, which writes no installation row: this executor never backfills a
/// row onto an old database. Calling <see cref="ExecuteAsync"/> without a qualified observation
/// in this scope, or after observing <see cref="MigrationObservationState.CurrentVersionCompatible"/>,
/// <see cref="MigrationObservationState.VersionTooNew"/>, or
/// <see cref="MigrationObservationState.InspectionFailed"/>, is a fixed failure with no side
/// effects.
/// </para>
/// <para>
/// One instance belongs to exactly one orchestration scope; the caller owns serialization
/// through a real migration lock, the context, and its lifecycle. Setup Codes are never issued
/// here, and consumer business data is never saved.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlInstallationInitializationExecutor
    : IDatabaseMigrationExecutor
{
    private readonly ReferencePostgreSqlDbContext context;
    private readonly ReferencePostgreSqlMigrationExecutor schemaExecutor;
    private readonly IServiceInstallationStore installationStore;
    private readonly ServiceId serviceId;
    private MigrationObservationState? lastObservation;

    /// <summary>Creates a composite initialization executor over the schema executor's build.</summary>
    /// <param name="context">The consumer's own context. It owns every save and transaction.</param>
    /// <param name="schemaExecutor">
    /// The schema-only executor whose observation and migration this executor composes with.
    /// </param>
    /// <param name="installationStore">
    /// The store that writes the initial pending installation row inside this executor's
    /// transaction.
    /// </param>
    /// <param name="serviceId">The service the initial installation row belongs to.</param>
    public ReferencePostgreSqlInstallationInitializationExecutor(
        ReferencePostgreSqlDbContext context,
        ReferencePostgreSqlMigrationExecutor schemaExecutor,
        IServiceInstallationStore installationStore,
        ServiceId serviceId)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(schemaExecutor);
        ArgumentNullException.ThrowIfNull(installationStore);
        ArgumentNullException.ThrowIfNull(serviceId);

        this.context = context;
        this.schemaExecutor = schemaExecutor;
        this.installationStore = installationStore;
        this.serviceId = serviceId;
    }

    /// <inheritdoc />
    /// <remarks>
    /// The observation is the schema executor's own and is recorded as this scope's last
    /// observation once it completes. An observation that ends in cancellation is not recorded:
    /// the last completed observation keeps deciding whether execution is qualified.
    /// </remarks>
    public async ValueTask<MigrationObservationState> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        var state = await schemaExecutor.InspectAsync(cancellationToken).ConfigureAwait(false);
        lastObservation = state;
        return state;
    }

    /// <inheritdoc />
    /// <exception cref="ReferencePostgreSqlInstallationInitializationFailedException">
    /// The initialization did not complete. The failure carries a fixed message and no provider
    /// text.
    /// </exception>
    /// <remarks>
    /// <para>
    /// The caller's token is read once more after the single commit returns, so a cancellation
    /// requested by that checkpoint is reported with the caller's own token instead of a
    /// completed initialization. It says nothing about whether the commit already happened: a
    /// cancelled initialization is not a rolled-back one.
    /// </para>
    /// <para>
    /// A cancellation the caller did not request - an internal cancellation from the provider,
    /// the script, or the store - is an ordinary failure like any other, not the caller's
    /// cancellation.
    /// </para>
    /// </remarks>
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (lastObservation is not (MigrationObservationState.Empty
            or MigrationObservationState.PendingMigration))
        {
            throw new ReferencePostgreSqlInstallationInitializationFailedException();
        }

        if (lastObservation == MigrationObservationState.PendingMigration)
        {
            await schemaExecutor.ExecuteAsync(cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await using var transaction = await context.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            var script = context.GetService<IMigrator>()
                .GenerateScript(options: MigrationsSqlGenerationOptions.NoTransactions);
            await context.Database
                .ExecuteSqlRawAsync(script, cancellationToken)
                .ConfigureAwait(false);
            await installationStore
                .CreatePendingAsync(serviceId, cancellationToken)
                .ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // A script, store, or commit failure rolls back with the transaction's disposal. The
            // provider's own text, a connection secret, or an inner exception has nowhere to
            // travel through this failure, and nothing about how much DDL had already run is
            // claimed either way.
            throw new ReferencePostgreSqlInstallationInitializationFailedException();
        }

        // The single completion checkpoint: every committed initialization passes through it,
        // and a cancellation observed here does not roll anything back.
        cancellationToken.ThrowIfCancellationRequested();
    }
}

/// <summary>
/// Reports that the reference service's PostgreSQL installation initialization did not complete.
/// </summary>
/// <remarks>
/// The message is fixed and carries neither the provider's own text nor an inner exception, so a
/// connection secret or a server message cannot reach a diagnostic through this failure. It says
/// nothing about how much of the initialization was already committed.
/// </remarks>
public sealed class ReferencePostgreSqlInstallationInitializationFailedException : Exception
{
    private const string FixedMessage =
        "The reference service PostgreSQL installation initialization did not complete.";

    /// <summary>Creates the failure with its fixed message.</summary>
    public ReferencePostgreSqlInstallationInitializationFailedException()
        : base(FixedMessage)
    {
    }
}
