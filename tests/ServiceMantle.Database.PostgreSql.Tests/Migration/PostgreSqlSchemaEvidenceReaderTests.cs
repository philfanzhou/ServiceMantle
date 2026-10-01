using Npgsql;
using ServiceMantle.Database.PostgreSql.Migration;
using ServiceMantle.Migration;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests.Migration;

/// <summary>
/// Argument and entry-checkpoint behavior of <see cref="PostgreSqlSchemaEvidenceReader"/> that
/// needs no database.
/// </summary>
public sealed class PostgreSqlSchemaEvidenceReaderContractTests
{
    [Fact]
    public async Task ReadAsync_rejects_a_null_connection()
    {
        var reader = new PostgreSqlSchemaEvidenceReader();

        await Assert.ThrowsAsync<ArgumentNullException>(
            () => reader.ReadAsync(null!, TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task ReadAsync_cancels_at_the_entry_checkpoint_before_opening()
    {
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var reader = new PostgreSqlSchemaEvidenceReader();
        await using var connection = new NpgsqlConnection("Host=127.0.0.1;Port=1;Database=x");

        // The entry checkpoint fires before the connection is opened: the connection stays
        // closed and no result fact is produced.
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(connection, cancellation.Token));

        Assert.Equal(System.Data.ConnectionState.Closed, connection.State);
    }
}

/// <summary>
/// Real PostgreSQL coverage for <see cref="PostgreSqlSchemaEvidenceReader"/> (Testcontainers).
/// Enable with RUN_SERVICEMANTLE_POSTGRES_TESTS=true: every snapshot dimension against a seeded
/// database, the two failure facts (3D000 target missing versus authentication/network read
/// failures) with identifier-only messages, the missing-history-table and empty-database reads,
/// and the cancellation checkpoints.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class PostgreSqlSchemaEvidenceReaderTests : IAsyncLifetime
{
    private const string CorrectPassword = "test-password";
    private const string WrongPassword = "wrong-password";

    private PostgreSqlContainer? container;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRunPostgreSqlTests())
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("evidence_reader")
            .WithUsername("test-user")
            .WithPassword(CorrectPassword)
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(TestContext.Current.CancellationToken);
            await container.DisposeAsync();
        }
    }

    [Fact]
    public async Task Every_snapshot_dimension_reads_correctly_from_a_seeded_database()
    {
        var connectionString = await CreateSeededDatabaseAsync("full_dimension");

        var result = await ReadAsync(connectionString, TestContext.Current.CancellationToken);

        Assert.Equal(SchemaEvidenceReadState.Succeeded, result.State);
        Assert.Equal(
            ["20260101000000_InitialCreate", "20260102000000_Second", "20260103000000_Third"],
            result.AppliedMigrationIds);

        var snapshot = result.Snapshot!;
        Assert.Equal(
            ["app.children", "app.parents", "public.defaults_table"],
            snapshot.Tables.Select(table => table.ToString()));

        // Columns: name, format_type type string, nullability, both identity kinds, serial
        // (identity None with a stored default — the evidence bit is independent of identity),
        // and the configured defaults.
        var parents = snapshot.Tables.Single(table => table.Name == "parents");
        var parentColumns = parents.Columns;
        Assert.Equal(8, parentColumns.Count);
        var id = Assert.Single(parentColumns, column => column.Name == "id");
        Assert.Equal("bigint", id.DataType);
        Assert.False(id.IsNullable);
        Assert.Equal(SchemaIdentityKind.Always, id.IdentityKind);
        Assert.False(id.HasStoredDefault);
        var byDefault = Assert.Single(parentColumns, column => column.Name == "bydef");
        Assert.Equal(SchemaIdentityKind.ByDefault, byDefault.IdentityKind);
        var realSerial = Assert.Single(parentColumns, column => column.Name == "real_serial");
        Assert.Equal(SchemaIdentityKind.None, realSerial.IdentityKind);
        Assert.True(realSerial.HasStoredDefault);
        var withDefault = Assert.Single(parentColumns, column => column.Name == "with_default");
        Assert.True(withDefault.HasStoredDefault);
        Assert.True(withDefault.IsNullable);
        var withDefaultSql = Assert.Single(parentColumns, column => column.Name == "with_default_sql");
        Assert.Equal("timestamp with time zone", withDefaultSql.DataType);
        Assert.True(withDefaultSql.HasStoredDefault);
        var plain = Assert.Single(parentColumns, column => column.Name == "plain");
        Assert.False(plain.HasStoredDefault);
        Assert.True(plain.IsNullable);

        // Primary keys keep the constraint order.
        Assert.NotNull(parents.PrimaryKey);
        Assert.Equal(["tenant_id", "code"], parents.PrimaryKey!.Columns);
        var children = snapshot.Tables.Single(table => table.Name == "children");
        Assert.Equal(["id"], children.PrimaryKey!.Columns);
        var defaultsTable = snapshot.Tables.Single(table => table.Name == "defaults_table");
        Assert.Equal(["id"], defaultsTable.PrimaryKey!.Columns);

        // Foreign keys: shape, referenced schema/table/columns, and every delete rule; no
        // constraint name is part of the model.
        var foreignKeys = children.ForeignKeys;
        Assert.Equal(5, foreignKeys.Count);
        var cascade = Assert.Single(
            foreignKeys, key => key.DeleteRule == SchemaForeignKeyDeleteRule.Cascade);
        Assert.Equal(["parent_tenant", "parent_code"], cascade.Columns);
        Assert.Equal("parents", cascade.ReferencedTable);
        Assert.Equal("app", cascade.ReferencedSchema);
        Assert.Equal(["tenant_id", "code"], cascade.ReferencedColumns);
        Assert.Single(foreignKeys, key => key.DeleteRule == SchemaForeignKeyDeleteRule.NoAction);
        Assert.Single(foreignKeys, key => key.DeleteRule == SchemaForeignKeyDeleteRule.SetNull);
        Assert.Single(foreignKeys, key => key.DeleteRule == SchemaForeignKeyDeleteRule.SetDefault);
        Assert.Single(foreignKeys, key => key.DeleteRule == SchemaForeignKeyDeleteRule.Restrict);
        Assert.Empty(parents.ForeignKeys);
        Assert.Empty(defaultsTable.ForeignKeys);

        // Non-constraint indexes: columns and uniqueness. The unique index, the two-column plain
        // index; the expression index and every constraint-backing index stay outside the model.
        var indexes = children.Indexes;
        Assert.Equal(2, indexes.Count);
        var plainIndex = Assert.Single(indexes, index => !index.IsUnique);
        Assert.Equal(["parent_tenant", "id"], plainIndex.Columns);
        var uniqueIndex = Assert.Single(indexes, index => index.IsUnique);
        Assert.Equal(["other_id"], uniqueIndex.Columns);
        Assert.Empty(parents.Indexes);
    }

    [Fact]
    public async Task A_database_without_tables_or_history_reads_as_empty_success()
    {
        var connectionString = await CreateEmptyDatabaseAsync("empty_target");

        var result = await ReadAsync(connectionString, TestContext.Current.CancellationToken);

        Assert.Equal(SchemaEvidenceReadState.Succeeded, result.State);
        Assert.Empty(result.AppliedMigrationIds);
        Assert.Empty(result.Snapshot!.Tables);
    }

    [Fact]
    public async Task A_missing_target_database_returns_the_missing_fact_with_an_identifier_only_message()
    {
        RequireContainer();
        var connectionString = new NpgsqlConnectionStringBuilder(container!.GetConnectionString())
        {
            Database = "evidence_reader_missing_target",
        }.ConnectionString;

        var result = await ReadAsync(connectionString, TestContext.Current.CancellationToken);

        Assert.Equal(SchemaEvidenceReadState.TargetDatabaseMissing, result.State);
        Assert.Null(result.Snapshot);
        Assert.Empty(result.AppliedMigrationIds);
        Assert.Equal(
            "The target database 'evidence_reader_missing_target' does not exist.",
            result.Message);
        AssertIdentifierOnlyMessage(result.Message);
    }

    [Fact]
    public async Task An_authentication_failure_returns_read_failed_and_never_missing()
    {
        RequireContainer();
        var connectionString = new NpgsqlConnectionStringBuilder(container!.GetConnectionString())
        {
            Password = WrongPassword,
        }.ConnectionString;

        var result = await ReadAsync(connectionString, TestContext.Current.CancellationToken);

        Assert.Equal(SchemaEvidenceReadState.ReadFailed, result.State);
        Assert.Null(result.Snapshot);
        Assert.Equal(
            "The schema evidence of 'evidence_reader' could not be read.",
            result.Message);
        AssertIdentifierOnlyMessage(result.Message);
    }

    [Fact]
    public async Task A_network_failure_returns_read_failed_and_never_missing()
    {
        var connectionString = "Host=127.0.0.1;Port=1;Database=evidence_reader;"

            // A short timeout keeps the refused-connection attempt from waiting on the driver
            // default; the port is closed, so no server ever answers.
            + "Timeout=2;Max Auto Prepare=0";

        var result = await ReadAsync(connectionString, TestContext.Current.CancellationToken);

        Assert.Equal(SchemaEvidenceReadState.ReadFailed, result.State);
        Assert.Null(result.Snapshot);
        Assert.Equal(
            "The schema evidence of 'evidence_reader' could not be read.",
            result.Message);
        AssertIdentifierOnlyMessage(result.Message);
    }

    [Fact]
    public async Task An_in_flight_cancellation_propagates_and_stops_the_read()
    {
        var connectionString = await CreateSeededDatabaseAsync("cancel_mid_read");

        // A second session holds an ACCESS EXCLUSIVE lock on the history table inside its own
        // transaction, so the reader's first query blocks; the caller's cancellation aborts it
        // before any later query runs.
        await using var blocker = new NpgsqlConnection(connectionString);
        await blocker.OpenAsync(TestContext.Current.CancellationToken);
        await using var blockerTransaction = await blocker.BeginTransactionAsync(
            TestContext.Current.CancellationToken);
        await using (var lockCommand = blocker.CreateCommand())
        {
            lockCommand.Transaction = blockerTransaction;
            lockCommand.CommandText =
                "LOCK TABLE \"__EFMigrationsHistory\" IN ACCESS EXCLUSIVE MODE";
            await lockCommand.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(2));
        await using var connection = new NpgsqlConnection(connectionString);
        var reader = new PostgreSqlSchemaEvidenceReader();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => reader.ReadAsync(connection, cancellation.Token));

        await blockerTransaction.RollbackAsync(TestContext.Current.CancellationToken);

        // The connection is still usable and the same evidence reads completely once nothing
        // blocks it: the aborted read left no partial state.
        var retry = await ReadAsync(connectionString, TestContext.Current.CancellationToken);
        Assert.Equal(SchemaEvidenceReadState.Succeeded, retry.State);
        Assert.Equal(3, retry.AppliedMigrationIds.Count);
    }

    private async Task<SchemaEvidenceReadResult> ReadAsync(
        string connectionString,
        CancellationToken cancellationToken)
    {
        RequireContainer();
        var reader = new PostgreSqlSchemaEvidenceReader();
        await using var connection = new NpgsqlConnection(connectionString);
        return await reader.ReadAsync(connection, cancellationToken);
    }

    private async Task<string> CreateSeededDatabaseAsync(string suffix)
    {
        var databaseName = $"evidence_reader_{suffix}_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(databaseName);
        await ExecuteAsync(GetDatabaseConnectionString(databaseName), """
            CREATE SCHEMA app;
            CREATE TABLE app.parents (
                tenant_id text NOT NULL,
                code text NOT NULL,
                id bigint GENERATED ALWAYS AS IDENTITY,
                bydef bigint GENERATED BY DEFAULT AS IDENTITY,
                real_serial bigserial NOT NULL,
                with_default integer DEFAULT 7,
                with_default_sql timestamptz DEFAULT now(),
                plain text,
                CONSTRAINT pk_parents PRIMARY KEY (tenant_id, code),
                CONSTRAINT uq_parents_id UNIQUE (id),
                CONSTRAINT uq_parents_bydef UNIQUE (bydef),
                CONSTRAINT uq_parents_serial UNIQUE (real_serial)
            );
            CREATE TABLE app.children (
                id bigint GENERATED BY DEFAULT AS IDENTITY,
                parent_tenant text NOT NULL,
                parent_code text NOT NULL,
                other_id bigint,
                setnull_id bigint,
                setdefault_id integer DEFAULT 0,
                restrict_id bigint,
                CONSTRAINT pk_children PRIMARY KEY (id),
                CONSTRAINT fk_cascade FOREIGN KEY (parent_tenant, parent_code)
                    REFERENCES app.parents (tenant_id, code) ON DELETE CASCADE,
                CONSTRAINT fk_noaction FOREIGN KEY (other_id)
                    REFERENCES app.parents (id) ON DELETE NO ACTION,
                CONSTRAINT fk_setnull FOREIGN KEY (setnull_id)
                    REFERENCES app.parents (real_serial) ON DELETE SET NULL,
                CONSTRAINT fk_setdefault FOREIGN KEY (setdefault_id)
                    REFERENCES app.parents (bydef) ON DELETE SET DEFAULT,
                CONSTRAINT fk_restrict FOREIGN KEY (restrict_id)
                    REFERENCES app.parents (id) ON DELETE RESTRICT
            );
            CREATE INDEX ix_children_plain ON app.children (parent_tenant, id);
            CREATE UNIQUE INDEX ux_children_other ON app.children (other_id);
            CREATE UNIQUE INDEX ux_children_expr ON app.children (lower(parent_code));
            CREATE TABLE public.defaults_table (
                id bigint GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                with_default integer DEFAULT 7
            );
            CREATE TABLE "__EFMigrationsHistory" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId")
            );
            INSERT INTO "__EFMigrationsHistory"
                ("MigrationId", "ProductVersion")
            VALUES
                ('20260102000000_Second', '10.0.11'),
                ('20260103000000_Third', '10.0.11'),
                ('20260101000000_InitialCreate', '10.0.11');
            """);
        return GetDatabaseConnectionString(databaseName);
    }

    private async Task<string> CreateEmptyDatabaseAsync(string suffix)
    {
        var databaseName = $"evidence_reader_{suffix}_{Guid.NewGuid():N}";
        await CreateDatabaseAsync(databaseName);
        return GetDatabaseConnectionString(databaseName);
    }

    private async Task CreateDatabaseAsync(string databaseName)
    {
        RequireContainer();
        await ExecuteAsync(GetMasterConnectionString(), $"CREATE DATABASE {databaseName}");
    }

    private async Task ExecuteAsync(string connectionString, string commandText)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = commandText;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private string GetMasterConnectionString() =>
        new NpgsqlConnectionStringBuilder(container!.GetConnectionString())
        {
            Database = "evidence_reader",
        }.ConnectionString;

    private string GetDatabaseConnectionString(string databaseName) =>
        new NpgsqlConnectionStringBuilder(container!.GetConnectionString())
        {
            Database = databaseName,
        }.ConnectionString;

    private void RequireContainer() => RealDatabaseTestEnvironment.RequireAvailable(
        RealDatabaseProvider.PostgreSql,
        container is not null);

    private static void AssertIdentifierOnlyMessage(string message)
    {
        // Negative assertions: the rendered message carries no SQL text, no connection values,
        // no credentials, and no exception text — only the identifier.
        Assert.DoesNotContain("SELECT", message, StringComparison.Ordinal);
        Assert.DoesNotContain("INSERT", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Host=", message, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", message, StringComparison.Ordinal);
        Assert.DoesNotContain(CorrectPassword, message, StringComparison.Ordinal);
        Assert.DoesNotContain(WrongPassword, message, StringComparison.Ordinal);
        Assert.DoesNotContain("5432", message, StringComparison.Ordinal);
        Assert.DoesNotContain("test-user", message, StringComparison.Ordinal);
    }

    private static bool ShouldRunPostgreSqlTests() =>
        Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS")
            ?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";
}
