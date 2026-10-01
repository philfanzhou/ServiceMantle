using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using ServiceMantle.Persistence.Relational.Migration;
using Xunit;

namespace ServiceMantle.Persistence.Relational.Tests.Migration;

/// <summary>
/// Real-database behavior tests for <see cref="EfCoreMigrationBaselineWriter"/> on SQLite
/// (shared in-memory, no Docker): first write, idempotent re-stamp, the configured history table
/// name, parameterized values, transaction rollback on failure and cancellation, the entry
/// cancellation checkpoint, and value validation.
/// </summary>
public sealed class EfCoreMigrationBaselineWriterSqliteTests : IAsyncLifetime
{
    private const string MigrationId = "20260101000000_InitialCreate";
    private const string ProductVersion = "10.0.11";

    private SqliteConnection keepAlive = null!;
    private string connectionString = null!;

    public ValueTask InitializeAsync()
    {
        // A shared-cache in-memory database survives across connections while the keep-alive
        // connection stays open, so the writer can run on its own connection and the assertions
        // can read the result through another.
        connectionString =
            $"Data Source=file:baseline_writer_{Guid.NewGuid():N}?mode=memory&cache=shared";
        keepAlive = new SqliteConnection(connectionString);
        keepAlive.Open();
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        await keepAlive.DisposeAsync();
    }

    [Fact]
    public async Task First_write_creates_the_history_table_and_stamps_the_row()
    {
        await using var context = CreateDefaultContext();
        var writer = new EfCoreMigrationBaselineWriter(context);
        await using var connection = new SqliteConnection(connectionString);

        var inserted = await writer.WriteBaselineAsync(
            connection, MigrationId, ProductVersion, TestContext.Current.CancellationToken);

        Assert.True(inserted);
        Assert.Equal((MigrationId, ProductVersion), ReadRow("__EFMigrationsHistory"));
        // The writer opened the closed connection itself and closed it again.
        Assert.Equal(ConnectionState.Closed, connection.State);
    }

    [Fact]
    public async Task Repeated_write_of_the_same_id_is_idempotent_and_does_not_overwrite()
    {
        await using var context = CreateDefaultContext();
        var writer = new EfCoreMigrationBaselineWriter(context);
        await using var connection = new SqliteConnection(connectionString);
        await writer.WriteBaselineAsync(
            connection, MigrationId, ProductVersion, TestContext.Current.CancellationToken);

        var reStamped = await writer.WriteBaselineAsync(
            connection, MigrationId, "9.9.9-overwrite-attempt", TestContext.Current.CancellationToken);

        Assert.False(reStamped);
        Assert.Equal((MigrationId, ProductVersion), ReadRow("__EFMigrationsHistory"));
    }

    [Fact]
    public async Task Configured_history_table_name_is_respected()
    {
        await using var context = new CustomHistoryContext(
            new DbContextOptionsBuilder<CustomHistoryContext>()
                .UseSqlite(
                    "Data Source=:memory:",
                    sqlite => sqlite.MigrationsHistoryTable("baseline_history"))
                .Options);
        var writer = new EfCoreMigrationBaselineWriter(context);
        await using var connection = new SqliteConnection(connectionString);

        var inserted = await writer.WriteBaselineAsync(
            connection, MigrationId, ProductVersion, TestContext.Current.CancellationToken);

        Assert.True(inserted);
        Assert.Equal((MigrationId, ProductVersion), ReadRow("baseline_history"));
        Assert.False(TableExists("__EFMigrationsHistory"));
    }

    [Fact]
    public async Task The_insert_statement_carries_the_values_as_parameters()
    {
        var connection = new InstrumentedConnection(connectionString);
        await using (connection)
        {
            await using var context = CreateDefaultContext();
            var writer = new EfCoreMigrationBaselineWriter(context);
            await writer.WriteBaselineAsync(
                connection, MigrationId, ProductVersion, TestContext.Current.CancellationToken);
        }

        var insert = connection.CommandTexts.Single(text => text.StartsWith("INSERT INTO", StringComparison.Ordinal));
        // The statement text carries the id nowhere: it travels as a command parameter, so no
        // failure diagnostic can echo it from the composed SQL.
        Assert.DoesNotContain(MigrationId, insert, StringComparison.Ordinal);
        Assert.DoesNotContain(ProductVersion, insert, StringComparison.Ordinal);
        Assert.Contains("@migrationId", insert, StringComparison.Ordinal);
        Assert.Contains("@productVersion", insert, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Failure_after_the_create_rolls_back_the_half_created_table()
    {
        var failure = new InvalidOperationException("injected insert failure");
        var connection = new InstrumentedConnection(
            connectionString,
            command =>
            {
                if (command.CommandText.StartsWith("INSERT INTO", StringComparison.Ordinal))
                {
                    throw failure;
                }
            });
        await using (connection)
        {
            await using var context = CreateDefaultContext();
            var writer = new EfCoreMigrationBaselineWriter(context);

            var observed = await Assert.ThrowsAsync<InvalidOperationException>(
                () => writer.WriteBaselineAsync(
                    connection, MigrationId, ProductVersion, TestContext.Current.CancellationToken));

            Assert.Same(failure, observed);
        }

        // The rollback undid the create-if-not-exists as well: no half-created table or row.
        Assert.False(TableExists("__EFMigrationsHistory"));

        // A retry on a clean connection starts from the rolled-back state and succeeds.
        await using var retryContext = CreateDefaultContext();
        var retryWriter = new EfCoreMigrationBaselineWriter(retryContext);
        await using var retryConnection = new SqliteConnection(connectionString);
        Assert.True(await retryWriter.WriteBaselineAsync(
            retryConnection, MigrationId, ProductVersion, TestContext.Current.CancellationToken));
        Assert.Equal((MigrationId, ProductVersion), ReadRow("__EFMigrationsHistory"));
    }

    [Fact]
    public async Task Entry_cancellation_runs_no_commands_and_throws_operation_canceled()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var connection = new InstrumentedConnection(connectionString);
        await using (connection)
        {
            await using var context = CreateDefaultContext();
            var writer = new EfCoreMigrationBaselineWriter(context);

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => writer.WriteBaselineAsync(
                    connection, MigrationId, ProductVersion, cancellation.Token));
        }

        Assert.Empty(connection.CommandTexts);
        Assert.False(TableExists("__EFMigrationsHistory"));
    }

    [Fact]
    public async Task In_flight_cancellation_rolls_back_and_leaves_no_trace()
    {
        using var cancellation = new CancellationTokenSource();
        var connection = new InstrumentedConnection(
            connectionString,
            _ => cancellation.Cancel());
        await using (connection)
        {
            await using var context = CreateDefaultContext();
            var writer = new EfCoreMigrationBaselineWriter(context);

            // The create command executes and flips the token; the insert command that follows
            // observes the cancelled token, and the whole transaction rolls back.
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => writer.WriteBaselineAsync(
                    connection, MigrationId, ProductVersion, cancellation.Token));
        }

        Assert.False(TableExists("__EFMigrationsHistory"));
    }

    [Fact]
    public async Task A_connection_with_its_own_transaction_surfaces_the_provider_failure()
    {
        await using var context = CreateDefaultContext();
        var writer = new EfCoreMigrationBaselineWriter(context);
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var ownTransaction = await connection.BeginTransactionAsync(
            TestContext.Current.CancellationToken);

        // The writer cannot start an independent transaction on this connection; the provider's
        // own failure surfaces and nothing is written.
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => writer.WriteBaselineAsync(
                connection, MigrationId, ProductVersion, TestContext.Current.CancellationToken));
        Assert.False(TableExists("__EFMigrationsHistory"));
    }

    [Fact]
    public async Task Invalid_values_are_rejected_before_any_command()
    {
        await using var context = CreateDefaultContext();
        var writer = new EfCoreMigrationBaselineWriter(context);
        var connection = new InstrumentedConnection(connectionString);
        await using (connection)
        {
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => writer.WriteBaselineAsync(null!, MigrationId, ProductVersion));
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => writer.WriteBaselineAsync(connection, null!, ProductVersion));
            await Assert.ThrowsAsync<ArgumentNullException>(
                () => writer.WriteBaselineAsync(connection, MigrationId, null!));
            await Assert.ThrowsAsync<ArgumentException>(
                () => writer.WriteBaselineAsync(connection, "", ProductVersion));
            await Assert.ThrowsAsync<ArgumentException>(
                () => writer.WriteBaselineAsync(connection, new string('x', 129), ProductVersion));
            await Assert.ThrowsAsync<ArgumentException>(
                () => writer.WriteBaselineAsync(connection, "bad\u0000id", ProductVersion));
            await Assert.ThrowsAsync<ArgumentException>(
                () => writer.WriteBaselineAsync(connection, MigrationId, " "));
        }

        Assert.Empty(connection.CommandTexts);
    }

    [Fact]
    public void Constructor_rejects_a_null_context()
    {
        Assert.Throws<ArgumentNullException>(() => new EfCoreMigrationBaselineWriter(null!));
    }

    private static DefaultHistoryContext CreateDefaultContext() =>
        new(new DbContextOptionsBuilder<DefaultHistoryContext>()
            .UseSqlite("Data Source=:memory:")
            .Options);

    private (string MigrationId, string ProductVersion)? ReadRow(string table)
    {
        Assert.True(TableExists(table));
        using var command = keepAlive.CreateCommand();
        command.CommandText = $"SELECT \"MigrationId\", \"ProductVersion\" FROM \"{table}\"";
        using var reader = command.ExecuteReader();
        if (!reader.Read())
        {
            return null;
        }

        return (reader.GetString(0), reader.GetString(1));
    }

    private bool TableExists(string table)
    {
        using var command = keepAlive.CreateCommand();
        command.CommandText =
            "SELECT 1 FROM sqlite_master WHERE type = 'table' AND name = $name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "$name";
        parameter.Value = table;
        command.Parameters.Add(parameter);
        return command.ExecuteScalar() is not null;
    }

    private sealed class DefaultHistoryContext(DbContextOptions<DefaultHistoryContext> options)
        : DbContext(options)
    {
    }

    private sealed class CustomHistoryContext(DbContextOptions<CustomHistoryContext> options)
        : DbContext(options)
    {
    }

    /// <summary>
    /// A pass-through connection that records executed command texts and lets a test inject a
    /// callback before each command (to fail one statement or flip a cancellation token).
    /// Transactions pass through unwrapped, so the writer's transaction lands on the real
    /// connection.
    /// </summary>
    private sealed class InstrumentedConnection : DbConnection
    {
        private readonly SqliteConnection inner;
        private readonly Action<DbCommand>? beforeCommand;

        internal List<string> CommandTexts { get; } = [];

        internal InstrumentedConnection(string connectionString, Action<DbCommand>? beforeCommand = null)
        {
            inner = new SqliteConnection(connectionString);
            this.beforeCommand = beforeCommand;
        }

        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value;
        }

        public override string Database => inner.Database;
        public override string DataSource => inner.DataSource;
        public override string ServerVersion => inner.ServerVersion;
        public override ConnectionState State => inner.State;

        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);
        public override void Close() => inner.Close();
        public override void Open() => inner.Open();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => new InstrumentedCommand(this, inner.CreateCommand());

        private sealed class InstrumentedCommand(
            InstrumentedConnection owner,
            DbCommand inner) : DbCommand
        {
            public override string CommandText
            {
                get => inner.CommandText;
                set => inner.CommandText = value;
            }

            public override int CommandTimeout
            {
                get => inner.CommandTimeout;
                set => inner.CommandTimeout = value;
            }

            public override CommandType CommandType
            {
                get => inner.CommandType;
                set => inner.CommandType = value;
            }

            public override bool DesignTimeVisible
            {
                get => inner.DesignTimeVisible;
                set => inner.DesignTimeVisible = value;
            }

            public override UpdateRowSource UpdatedRowSource
            {
                get => inner.UpdatedRowSource;
                set => inner.UpdatedRowSource = value;
            }

            private DbConnection? connection;
            private DbTransaction? transaction;

            protected override DbConnection? DbConnection
            {
                get => connection;
                set => connection = value;
            }

            protected override DbParameterCollection DbParameterCollection => inner.Parameters;

            protected override DbTransaction? DbTransaction
            {
                get => transaction;
                set
                {
                    transaction = value;
                    inner.Transaction = value;
                }
            }

            public override void Cancel() => inner.Cancel();
            public override int ExecuteNonQuery() => throw new NotSupportedException();
            public override object ExecuteScalar() => throw new NotSupportedException();
            public override void Prepare() => inner.Prepare();
            protected override DbParameter CreateDbParameter() => inner.CreateParameter();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior) =>
                throw new NotSupportedException();

            public override async Task<int> ExecuteNonQueryAsync(CancellationToken cancellationToken)
            {
                owner.CommandTexts.Add(CommandText);
                if (owner.beforeCommand is { } before)
                {
                    before(this);
                }

                return await inner.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }
}
