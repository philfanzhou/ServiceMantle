using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Data;

namespace ServiceMantle.ReferenceService.Database.Sqlite;

/// <summary>
/// The consumer-owned migration boundary for the sample's SQLite workspace database.
/// </summary>
/// <remarks>
/// <para>
/// The inspection opens its own read-only connection and never creates, writes, or repairs
/// anything: no <c>EnsureCreated</c>, no <c>Migrate</c>, no <c>SaveChanges</c>, and no sidecar of
/// its own. Execution is one <c>MigrateAsync</c> on the consumer's own context, over the writable
/// connection that cannot create the file implicitly.
/// </para>
/// <para>
/// The observation is deliberately conservative. It proves the finite states it can read from the
/// EF history table and the workspace table; a database whose history is unreadable, absent beside
/// application tables, or holds a record this build does not know is refused rather than adopted.
/// It does not prove an arbitrary physical schema, detect a hand-edited history, or claim data
/// compatibility.
/// </para>
/// </remarks>
public sealed class ReferenceSqliteMigrationExecutor : IDatabaseMigrationExecutor
{
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string WorkspaceTable = "reference_workspaces";

    private static readonly string[] RequiredWorkspaceColumns = ["Id", "DisplayName"];

    private readonly ReferenceDbContext context;
    private readonly ReferenceSqliteStartupOptions options;
    private readonly IReadOnlyList<string> knownMigrations;

    /// <summary>Creates an executor over this build's own migration set.</summary>
    public ReferenceSqliteMigrationExecutor(
        ReferenceDbContext context,
        ReferenceSqliteStartupOptions options)
        : this(context, options, knownMigrations: null)
    {
    }

    /// <summary>Creates an executor over an explicit migration set.</summary>
    /// <param name="context">The consumer's own context. It owns every save and transaction.</param>
    /// <param name="options">The explicit startup inputs.</param>
    /// <param name="knownMigrations">
    /// The migration ids this build knows, or null to read them from the context. An explicit set
    /// exists so the finite history matrix can be covered without adding a second production
    /// migration that the sample has no business need for.
    /// </param>
    public ReferenceSqliteMigrationExecutor(
        ReferenceDbContext context,
        ReferenceSqliteStartupOptions options,
        IReadOnlyList<string>? knownMigrations)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(options);
        this.context = context;
        this.options = options;
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
            await using var connection = new SqliteConnection(options.InspectionConnectionString);
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);

            var tables = await ReadTableNamesAsync(connection, cancellationToken).ConfigureAwait(false);
            var applicationTables = tables.Count(name =>
                !string.Equals(name, HistoryTable, StringComparison.Ordinal));
            if (!tables.Contains(HistoryTable, StringComparer.Ordinal))
            {
                // No history at all: an untouched database is adoptable, a database that already
                // holds tables from an unknown source is not.
                return applicationTables == 0
                    ? MigrationObservationState.Empty
                    : MigrationObservationState.InspectionFailed;
            }

            var applied = await ReadAppliedMigrationsAsync(connection, cancellationToken)
                .ConfigureAwait(false);
            if (applied.Any(id => !knownMigrations.Contains(id, StringComparer.Ordinal)))
            {
                // An unknown record is treated as a newer schema rather than guessed at.
                return MigrationObservationState.VersionTooNew;
            }

            if (applied.Count == 0)
            {
                return applicationTables == 0
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

            return await HasWorkspaceSchemaAsync(connection, cancellationToken).ConfigureAwait(false)
                ? MigrationObservationState.CurrentVersionCompatible
                : MigrationObservationState.InspectionFailed;
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
        catch (Exception)
        {
            // A history that cannot be read leaves the state unestablished; nothing is repaired.
            return MigrationObservationState.InspectionFailed;
        }
    }

    /// <inheritdoc />
    public async ValueTask ExecuteAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        await context.Database.MigrateAsync(cancellationToken).ConfigureAwait(false);
    }

    private static async Task<List<string>> ReadTableNamesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table' " +
            "AND name NOT LIKE 'sqlite_%'";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<List<string>> ReadAppliedMigrationsAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"SELECT \"MigrationId\" FROM \"{HistoryTable}\" ORDER BY \"MigrationId\"";
        var applied = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            applied.Add(reader.GetString(0));
        }

        applied.Sort(StringComparer.Ordinal);
        return applied;
    }

    private static async Task<bool> HasWorkspaceSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info(\"{WorkspaceTable}\")";
        var columns = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            columns.Add(reader.GetString(1));
        }

        return RequiredWorkspaceColumns.All(column =>
            columns.Contains(column, StringComparer.Ordinal));
    }
}
