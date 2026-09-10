using Microsoft.EntityFrameworkCore;
using Npgsql;
using ServiceMantle.Migration;

namespace ServiceMantle.ReferenceService.Database.PostgreSql;

/// <summary>
/// The consumer-owned schema migration boundary for the sample's PostgreSQL workspace database.
/// </summary>
/// <remarks>
/// <para>
/// This executor is schema-only. <see cref="InspectAsync"/> is read-only: it opens its own
/// connection, reads the catalog and the EF history table, and never creates, writes, or repairs
/// anything - no <c>EnsureCreated</c>, no <c>Migrate</c>, no <c>SaveChanges</c>. It does not create
/// the target database, and a missing database is reported, not provisioned.
/// <see cref="ExecuteAsync"/> runs exactly one explicit <c>MigrateAsync</c> on this context and
/// nothing else: it does not save consumer work, does not initialise installation state, and does
/// not complete Setup. A schema this executor reports as compatible is not an installation that is
/// <c>Completed</c> or <c>Ready</c>.
/// </para>
/// <para>
/// The observation is deliberately finite and covers only the <c>public</c> schema this reference
/// service owns. System schemas (<c>pg_catalog</c>, <c>information_schema</c>, and the
/// <c>pg_toast</c>/<c>pg_temp</c> families) are excluded from the relation census because they are
/// not application state; the EF history table is excluded from the application relation count
/// because it is this executor's own evidence. Anything else - an application relation in another
/// schema, a relation kind other than an ordinary table, an unreadable history, a history record
/// this build does not know, a gap in the applied sequence, or a missing table or column - is
/// refused rather than adopted.
/// </para>
/// <para>
/// The caller owns migration serialization, the authorization of the target, the context, and every
/// transaction. This executor registers no lock, holds nothing across calls, and cannot prevent
/// external DDL between an observation and an execution.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlMigrationExecutor : IDatabaseMigrationExecutor
{
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string WorkspaceTable = "reference_workspaces";
    private const string OwnedSchema = "public";
    private const string OrdinaryTable = "r";

    private static readonly string[] RequiredWorkspaceColumns = ["Id", "DisplayName"];

    private readonly ReferencePostgreSqlDbContext context;
    private readonly string inspectionConnectionString;
    private readonly IReadOnlyList<string> knownMigrations;

    /// <summary>Creates an executor over this build's own migration set.</summary>
    /// <param name="context">The consumer's own context. It owns every save and transaction.</param>
    /// <param name="inspectionConnectionString">
    /// The connection the read-only inspection opens for itself. It is never used to migrate.
    /// </param>
    public ReferencePostgreSqlMigrationExecutor(
        ReferencePostgreSqlDbContext context,
        string inspectionConnectionString)
        : this(context, inspectionConnectionString, knownMigrations: null)
    {
    }

    /// <summary>Creates an executor over an explicit migration set.</summary>
    /// <param name="context">The consumer's own context. It owns every save and transaction.</param>
    /// <param name="inspectionConnectionString">
    /// The connection the read-only inspection opens for itself. It is never used to migrate.
    /// </param>
    /// <param name="knownMigrations">
    /// The migration ids this build knows, or null to read them from the context. An explicit set
    /// exists so the finite history matrix can be covered without adding a second production
    /// migration that the sample has no business need for.
    /// </param>
    public ReferencePostgreSqlMigrationExecutor(
        ReferencePostgreSqlDbContext context,
        string inspectionConnectionString,
        IReadOnlyList<string>? knownMigrations)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentException.ThrowIfNullOrWhiteSpace(inspectionConnectionString);
        this.context = context;
        this.inspectionConnectionString = inspectionConnectionString;
        this.knownMigrations = (knownMigrations ?? [.. context.Database.GetMigrations()])
            .Order(StringComparer.Ordinal)
            .ToArray();
    }

    /// <inheritdoc />
    public async ValueTask<MigrationObservationState> InspectAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await using var connection = new NpgsqlConnection(inspectionConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            if (!await OwnedSchemaExistsAsync(connection, cancellationToken).ConfigureAwait(false))
            {
                // The schema this service owns is the only place it reads; without it there is no
                // observation to make and nothing is created to produce one.
                return MigrationObservationState.InspectionFailed;
            }

            var relations = await ReadRelationsAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (relations.Any(relation =>
                    !string.Equals(relation.Schema, OwnedSchema, StringComparison.Ordinal)))
            {
                // Application state outside the schema this service owns is never adopted.
                return MigrationObservationState.InspectionFailed;
            }

            var history = relations.FirstOrDefault(relation =>
                string.Equals(relation.Name, HistoryTable, StringComparison.Ordinal));
            var applicationRelations = relations
                .Where(relation =>
                    !string.Equals(relation.Name, HistoryTable, StringComparison.Ordinal))
                .ToArray();
            if (applicationRelations.Any(relation =>
                    !string.Equals(relation.Kind, OrdinaryTable, StringComparison.Ordinal)))
            {
                // A view, a materialized view, or a foreign table is outside the finite matrix.
                return MigrationObservationState.InspectionFailed;
            }

            if (history is null)
            {
                // No history at all: an untouched schema is adoptable, a schema that already holds
                // relations from an unknown source is not.
                return applicationRelations.Length == 0
                    ? MigrationObservationState.Empty
                    : MigrationObservationState.InspectionFailed;
            }

            if (!string.Equals(history.Kind, OrdinaryTable, StringComparison.Ordinal))
            {
                return MigrationObservationState.InspectionFailed;
            }

            cancellationToken.ThrowIfCancellationRequested();
            var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (applied.Any(id => !knownMigrations.Contains(id, StringComparer.Ordinal)))
            {
                // An unknown record is treated as a newer schema rather than guessed at. This is
                // decided on the record itself, never on an empty pending-migration list.
                return MigrationObservationState.VersionTooNew;
            }

            if (applied.Count == 0)
            {
                return applicationRelations.Length == 0
                    ? MigrationObservationState.Empty
                    : MigrationObservationState.InspectionFailed;
            }

            if (applied.Count < knownMigrations.Count)
            {
                return applied.SequenceEqual(
                    knownMigrations.Take(applied.Count),
                    StringComparer.Ordinal)
                    ? MigrationObservationState.PendingMigration
                    : MigrationObservationState.InspectionFailed;
            }

            return await HasWorkspaceSchemaAsync(connection, applicationRelations, cancellationToken)
                .ConfigureAwait(false)
                ? MigrationObservationState.CurrentVersionCompatible
                : MigrationObservationState.InspectionFailed;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // A catalog or a history that cannot be read leaves the state unestablished. The
            // provider's own text is deliberately not carried into the finite result.
            return MigrationObservationState.InspectionFailed;
        }
    }

    /// <inheritdoc />
    /// <exception cref="ReferencePostgreSqlMigrationFailedException">
    /// The migration did not complete. The failure carries a fixed message and no provider text.
    /// </exception>
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            throw new ReferencePostgreSqlMigrationFailedException();
        }
    }

    private static async Task<bool> OwnedSchemaExistsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"SELECT 1 FROM pg_catalog.pg_namespace WHERE nspname = '{OwnedSchema}'";
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) is not null;
    }

    private static async Task<List<Relation>> ReadRelationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT namespace.nspname, class.relname, class.relkind::text
            FROM pg_catalog.pg_class AS class
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = class.relnamespace
            WHERE class.relkind IN ('r', 'p', 'v', 'm', 'f')
              AND namespace.nspname NOT IN ('pg_catalog', 'information_schema')
              AND namespace.nspname NOT LIKE 'pg_toast%'
              AND namespace.nspname NOT LIKE 'pg_temp%'
            """;
        var relations = new List<Relation>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            relations.Add(new Relation(reader.GetString(0), reader.GetString(1), reader.GetString(2)));
        }

        return relations;
    }

    private static async Task<List<string>> ReadAppliedMigrationsAsync(
        NpgsqlConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            $"""SELECT "MigrationId" FROM "{OwnedSchema}"."{HistoryTable}" ORDER BY "MigrationId" """;
        var applied = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }

        applied.Sort(StringComparer.Ordinal);
        return applied;
    }

    private static async Task<bool> HasWorkspaceSchemaAsync(
        NpgsqlConnection connection,
        IReadOnlyList<Relation> applicationRelations,
        CancellationToken cancellationToken)
    {
        if (!applicationRelations.Any(relation =>
                string.Equals(relation.Name, WorkspaceTable, StringComparison.Ordinal)))
        {
            return false;
        }

        // The expected columns must be readable, not merely present in the catalog.
        await using var command = connection.CreateCommand();
        var projection = string.Join(", ", RequiredWorkspaceColumns.Select(column => $"\"{column}\""));
        command.CommandText =
            $"""SELECT {projection} FROM "{OwnedSchema}"."{WorkspaceTable}" WHERE false""";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken)
            .ConfigureAwait(false);
        return reader.FieldCount == RequiredWorkspaceColumns.Length;
    }

    private sealed record Relation(string Schema, string Name, string Kind);
}

/// <summary>
/// Reports that the reference service's PostgreSQL schema migration did not complete.
/// </summary>
/// <remarks>
/// The message is fixed and carries neither the provider's own text nor an inner exception, so a
/// connection secret or a server message cannot reach a diagnostic through this failure. It says
/// nothing about how much of the migration was already committed.
/// </remarks>
public sealed class ReferencePostgreSqlMigrationFailedException : Exception
{
    private const string FixedMessage =
        "The reference service PostgreSQL schema migration did not complete.";

    /// <summary>Creates the failure with its fixed message.</summary>
    public ReferencePostgreSqlMigrationFailedException()
        : base(FixedMessage)
    {
    }
}
