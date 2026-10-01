using System.Data;
using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.EntityFrameworkCore.Storage;

namespace ServiceMantle.Persistence.Relational.Migration;

/// <summary>
/// Writes one baseline migration id into the EF Core migrations history table on a
/// caller-owned connection: it creates the history table when missing (through the provider's
/// history repository) and inserts the row with one parameterized idempotent
/// <c>INSERT … SELECT … WHERE NOT EXISTS</c> statement, all inside one transaction the writer
/// owns and commits itself.
/// </summary>
/// <remarks>
/// <para>
/// Guarantees: a single call is atomic and idempotent — the create-if-not-exists script and the
/// insert run in one independent transaction, repeated calls neither duplicate the row nor fail,
/// and a failure or cancellation before the commit rolls the whole transaction back, leaving no
/// half-created table or row behind. Which id is written, and when, is always the caller's
/// decision.
/// </para>
/// <para>
/// The history table's name and schema are resolved through the provider's
/// <see cref="IHistoryRepository"/>: the create step executes the repository's own
/// create-if-not-exists script, and the insert targets the same configured table resolved from
/// the context's relational options, delimited with the provider's SQL generation helper.
/// </para>
/// <para>
/// Completion semantics: caller cancellation surfaces as
/// <see cref="OperationCanceledException"/> at the entry checkpoint and after any in-flight
/// command, distinct from provider failures, which propagate as the provider's own exception
/// after the rollback. The rollback is best-effort and never replaces the original exception.
/// </para>
/// <para>
/// Diagnostics: the writer never logs and never composes SQL from the values it is given — the
/// migration id and product version travel as command parameters, and only validated EF metadata
/// (the resolved table, schema, and column identifiers) is embedded in the statement text.
/// </para>
/// <para>
/// Non-guarantees: the writer does not verify that the written id matches the database's actual
/// structure ("evidence first, stamp second" is the caller's ordering); it does not roll back
/// side effects outside its own transaction; it does not cover a consumer bypassing this
    /// primitive and writing the history table itself; and a concurrent identical stamp relies on
    /// the history table's primary key — the losing insert surfaces as a provider exception after
    /// its rollback. The connection must carry no active transaction or ambient enlistment when
    /// the write starts; one that does surfaces as the provider's own failure when the writer
    /// starts its transaction. The write runs on providers whose dialect accepts a parameterized
    /// <c>SELECT</c> without <c>FROM</c> (PostgreSQL, SQL Server, SQLite are the verified set).
/// </para>
/// </remarks>
public sealed class EfCoreMigrationBaselineWriter
{
    private const string MigrationIdColumnName = "MigrationId";
    private const string ProductVersionColumnName = "ProductVersion";
    private const int MaximumValueLength = 128;

    private readonly string createIfNotExistsScript;
    private readonly string insertScript;
    private readonly string migrationIdParameterName;
    private readonly string productVersionParameterName;

    /// <summary>
    /// Initializes the writer from the consuming <see cref="DbContext"/>. The context is read
    /// for services only — its connection is never opened and the write never touches the
    /// context's own connection, transactions, or tracked state.
    /// </summary>
    /// <param name="context">The context whose history repository and relational options resolve
    /// the history table.</param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public EfCoreMigrationBaselineWriter(DbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var historyRepository = context.GetService<IHistoryRepository>();
        var sqlGenerationHelper = context.GetService<ISqlGenerationHelper>();
        // FindExtension matches the exact extension type, so a provider's derived relational
        // extension has to be located by its base type here.
        var relationalOptions = context.GetService<IDbContextOptions>()
            .Extensions
            .OfType<RelationalOptionsExtension>()
            .FirstOrDefault();
        var tableName = relationalOptions?.MigrationsHistoryTableName
            ?? HistoryRepository.DefaultTableName;
        var tableSchema = relationalOptions?.MigrationsHistoryTableSchema;

        var delimitedTable = sqlGenerationHelper.DelimitIdentifier(tableName, tableSchema);
        var delimitedMigrationId = sqlGenerationHelper.DelimitIdentifier(MigrationIdColumnName);
        var delimitedProductVersion = sqlGenerationHelper.DelimitIdentifier(ProductVersionColumnName);
        migrationIdParameterName = sqlGenerationHelper.GenerateParameterName("migrationId");
        productVersionParameterName = sqlGenerationHelper.GenerateParameterName("productVersion");
        var migrationIdPlaceholder = sqlGenerationHelper.GenerateParameterNamePlaceholder("migrationId");
        var productVersionPlaceholder = sqlGenerationHelper.GenerateParameterNamePlaceholder("productVersion");

        createIfNotExistsScript = historyRepository.GetCreateIfNotExistsScript();
        insertScript =
            $"INSERT INTO {delimitedTable} ({delimitedMigrationId}, {delimitedProductVersion}) " +
            $"SELECT {migrationIdPlaceholder}, {productVersionPlaceholder} " +
            $"WHERE NOT EXISTS (SELECT 1 FROM {delimitedTable} WHERE {delimitedMigrationId} = " +
            $"{migrationIdPlaceholder})";
    }

    /// <summary>
    /// Writes the baseline row on the caller-owned connection, in one independent transaction.
    /// </summary>
    /// <param name="connection">
    /// The connection to write through; opened when closed (and closed again) by this call, and
    /// required to have no active transaction of its own.
    /// </param>
    /// <param name="migrationId">The baseline migration id to stamp.</param>
    /// <param name="productVersion">
    /// The product version recorded with the id (the EF Core version the consumer's migrations
    /// were built with, by convention).
    /// </param>
    /// <param name="cancellationToken">Cancels the write at the entry checkpoint and any
    /// in-flight command.</param>
    /// <returns>
    /// <see langword="true"/> when this call inserted the row; <see langword="false"/> when the
    /// id was already present (the idempotent re-stamp).
    /// </returns>
    /// <exception cref="ArgumentNullException">
    /// <paramref name="connection"/> is null, or a value argument is null.
    /// </exception>
    /// <exception cref="ArgumentException">
    /// A value argument is empty, longer than 128 characters, or contains control characters.
    /// </exception>
    /// <exception cref="OperationCanceledException">
    /// The <paramref name="cancellationToken"/> was observed cancelled; nothing was committed.
    /// </exception>
    public async Task<bool> WriteBaselineAsync(
        DbConnection connection,
        string migrationId,
        string productVersion,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ValidateValue(migrationId, nameof(migrationId));
        ValidateValue(productVersion, nameof(productVersion));
        cancellationToken.ThrowIfCancellationRequested();

        var openedConnection = false;
        if (connection.State != ConnectionState.Open)
        {
            await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
            openedConnection = true;
        }

        try
        {
            await using var transaction = await connection
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);
            try
            {
                await ExecuteNonQueryAsync(
                    connection, transaction, createIfNotExistsScript, cancellationToken)
                    .ConfigureAwait(false);
                var insertedRows = await ExecuteInsertAsync(
                    connection, transaction, migrationId, productVersion, cancellationToken)
                    .ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
                return insertedRows != 0;
            }
            catch
            {
                await RollbackQuietlyAsync(transaction).ConfigureAwait(false);
                throw;
            }
        }
        finally
        {
            if (openedConnection)
            {
                await connection.CloseAsync().ConfigureAwait(false);
            }
        }
    }

    private async Task<int> ExecuteInsertAsync(
        DbConnection connection,
        DbTransaction transaction,
        string migrationId,
        string productVersion,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = insertScript;
            AddParameter(command, migrationIdParameterName, migrationId);
            AddParameter(command, productVersionParameterName, productVersion);
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static async Task<int> ExecuteNonQueryAsync(
        DbConnection connection,
        DbTransaction transaction,
        string commandText,
        CancellationToken cancellationToken)
    {
        var command = connection.CreateCommand();
        await using (command.ConfigureAwait(false))
        {
            command.Transaction = transaction;
            command.CommandText = commandText;
            return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    private static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    private static async Task RollbackQuietlyAsync(DbTransaction transaction)
    {
        try
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // The original failure is the fact this call reports; a rollback that also fails is
            // left to the transaction's own disposal, which providers roll back in this state.
        }
    }

    private static void ValidateValue(string? value, string parameterName)
    {
        if (value is null)
        {
            throw new ArgumentNullException(parameterName);
        }

        if (value.Length is < 1 or > MaximumValueLength ||
            string.IsNullOrWhiteSpace(value) ||
            value.Any(char.IsControl))
        {
            throw new ArgumentException(
                $"The {parameterName} must contain 1 to 128 visible characters and no control " +
                "characters.",
                parameterName);
        }
    }
}
