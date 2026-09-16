using System.Data.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Npgsql;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the finite schema states the sample's consumer-owned PostgreSQL migration executor
/// observes on a real PostgreSQL server, the one explicit schema migration it runs, and the
/// cancellation and failure boundaries around both. Nothing here wires the sample's host, initialises
/// installation state, or claims a schema is a completed installation.
/// </summary>
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePostgreSqlMigrationTests : IAsyncLifetime
{
    private const string WorkspaceMigration = "20260910000000_InitialReferencePostgreSqlWorkspace";
    private const string InstallationMigration = "20260912000000_AddReferencePostgreSqlInstallation";
    private const string DataProtectionMigration = "20260916000000_AddReferencePostgreSqlDataProtectionKeys";
    private const string SettingsAuditMigration = "20260917000000_AddReferencePostgreSqlSettingsAndAudit";
    // Synthetic ids dated after every real migration, so the applied history stays a strict prefix
    // of the test-owned known set.
    private const string FutureMigration = "20260918000000_FutureReferenceStep";
    private const string LaterMigration = "20260919000000_LaterReferenceStep";
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string WorkspaceTable = "reference_workspaces";
    private const string InstallationTable = "service_installations";
    private const string DataProtectionTable = "service_data_protection_keys";
    private const string SettingsTable = "service_settings";
    private const string AuditTable = "service_audit_logs";

    private static string[] KnownSet(params string[] extra) =>
        new[] { WorkspaceMigration, InstallationMigration, DataProtectionMigration, SettingsAuditMigration }
            .Concat(extra)
            .Order(StringComparer.Ordinal)
            .ToArray();

    // Rolls a fully migrated target back to the exact schema a build without the settings and
    // audit migration left behind: both new tables and their history record are gone.
    private async Task RollBackSettingsAndAuditAsync(string target)
    {
        await ExecuteAsync(target, $"""DROP TABLE public."{AuditTable}" """);
        await ExecuteAsync(target, $"""DROP TABLE public."{SettingsTable}" """);
        await ExecuteAsync(target, $"""
            DELETE FROM public."{HistoryTable}" WHERE "MigrationId" = '{SettingsAuditMigration}'
            """);
    }

    // The ServiceMantle installation table's columns, as named by the public EF Core persistence
    // package's own mapping; each missing one must independently refuse the observation.
    public static TheoryData<string> InstallationColumns() => new()
    {
        "service_id",
        "status",
        "created_at_utc",
        "completed_at_utc",
        "version",
        "setup_code_generation",
        "setup_code_digest",
        "setup_code_issued_at_utc",
        "setup_code_expires_at_utc",
    };

    // Synthetic fixture secrets. They exist only inside this container and are asserted to stay out
    // of the executor's own diagnostics.
    private const string SyntheticUser = "reference_pg_owner";
    private const string SyntheticPassword = "synthetic-reference-owner-secret";
    private const string SyntheticReaderRole = "reference_pg_stranger";
    private const string SyntheticReaderPassword = "synthetic-reference-stranger-secret";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? container;
    private string? maintenanceConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("servicemantle_reference_maintenance")
            .WithUsername(SyntheticUser)
            .WithPassword(SyntheticPassword)
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        maintenanceConnectionString = container.GetConnectionString();
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
    public async Task An_empty_target_is_adoptable_and_both_migrations_apply()
    {
        var target = await CreateTargetAsync("adoptable");
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        var initial = await executor.InspectAsync(Token);
        await executor.ExecuteAsync(Token);
        var afterExecution = await executor.InspectAsync(Token);
        var afterRepeatedInspection = await executor.InspectAsync(Token);

        Assert.Equal(MigrationObservationState.Empty, initial);
        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, afterExecution);
        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, afterRepeatedInspection);
        // The repeated observation did not migrate again, and both fixed versions are recorded.
        Assert.Equal(KnownSet(), await ReadHistoryAsync(target));
        Assert.False(context.Database.HasPendingModelChanges());
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Token));
    }

    [Fact]
    public async Task A_workspace_only_target_is_a_pending_migration_and_the_upgrade_keeps_rows()
    {
        var target = await CreateTargetAsync("workspace_only");
        await MigrateAsync(target);
        var kept = new ReferenceWorkspace { Id = Guid.NewGuid(), DisplayName = "kept" };
        await using (var seed = CreateContext(target))
        {
            seed.Workspaces.Add(kept);
            await seed.SaveChangesAsync(Token);
        }

        // Roll the schema back to exactly what an older workspace-only build left behind.
        await ExecuteAsync(target, $"""DROP TABLE public."{InstallationTable}" """);
        await ExecuteAsync(target, $"""
            DELETE FROM public."{HistoryTable}" WHERE "MigrationId" = '{InstallationMigration}'
            """);
        await ExecuteAsync(target, $"""DROP TABLE public."{DataProtectionTable}" """);
        await ExecuteAsync(target, $"""
            DELETE FROM public."{HistoryTable}" WHERE "MigrationId" = '{DataProtectionMigration}'
            """);
        await RollBackSettingsAndAuditAsync(target);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        var pending = await executor.InspectAsync(Token);

        Assert.Equal(MigrationObservationState.PendingMigration, pending);
        await executor.ExecuteAsync(Token);

        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, await executor.InspectAsync(Token));
        Assert.Equal(["kept"], await ReadWorkspaceNamesAsync(target));
        // The upgrade created the installation table but initialised no installation row.
        Assert.Equal(KnownSet(), await ReadHistoryAsync(target));
        Assert.Empty(await ReadInstallationServiceIdsAsync(target));
    }

    [Fact]
    public async Task A_two_migration_target_is_a_pending_upgrade_and_the_key_table_is_added()
    {
        var target = await CreateTargetAsync("two_migration_upgrade");
        await MigrateAsync(target);
        // Roll the schema back to exactly what a build without the data-protection migration left
        // behind: the key table and its history record are gone, both earlier tables remain.
        await ExecuteAsync(target, $"""DROP TABLE public."{DataProtectionTable}" """);
        await ExecuteAsync(target, $"""
            DELETE FROM public."{HistoryTable}" WHERE "MigrationId" = '{DataProtectionMigration}'
            """);
        await RollBackSettingsAndAuditAsync(target);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.PendingMigration, await executor.InspectAsync(Token));
        await executor.ExecuteAsync(Token);

        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, await executor.InspectAsync(Token));
        Assert.Equal(KnownSet(), await ReadHistoryAsync(target));
        // The upgrade created the key table and touched no earlier data.
        Assert.Contains(DataProtectionTable, await ReadRelationNamesAsync(target));
        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task A_three_migration_target_is_a_pending_upgrade_and_the_settings_and_audit_tables_are_added()
    {
        var target = await CreateTargetAsync("three_migration_upgrade");
        await MigrateAsync(target);
        // Roll the schema back to exactly what the current main build left behind: workspace,
        // installation, and key tables with their history, no settings or audit tables.
        await RollBackSettingsAndAuditAsync(target);
        var kept = new ReferenceWorkspace { Id = Guid.NewGuid(), DisplayName = "kept" };
        await using (var seed = CreateContext(target))
        {
            seed.Workspaces.Add(kept);
            await seed.SaveChangesAsync(Token);
        }

        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.PendingMigration, await executor.InspectAsync(Token));
        await executor.ExecuteAsync(Token);

        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, await executor.InspectAsync(Token));
        Assert.Equal(KnownSet(), await ReadHistoryAsync(target));
        var relations = await ReadRelationNamesAsync(target);
        Assert.Contains(SettingsTable, relations);
        Assert.Contains(AuditTable, relations);
        // The upgrade kept earlier data and wrote no settings or audit rows.
        Assert.Equal(["kept"], await ReadWorkspaceNamesAsync(target));
        await using (var probe = CreateContext(target))
        {
            await probe.Database.OpenConnectionAsync(Token);
            await using var auditCount = probe.Database.GetDbConnection().CreateCommand();
            auditCount.CommandText = $"""
                SELECT (SELECT count(*) FROM public."{SettingsTable}"), (SELECT count(*) FROM public."{AuditTable}")
                """;
            await using var reader = await auditCount.ExecuteReaderAsync(Token);
            Assert.True(await reader.ReadAsync(Token));
            Assert.Equal(0L, reader.GetInt64(0));
            Assert.Equal(0L, reader.GetInt64(1));
        }

        Assert.False(context.Database.HasPendingModelChanges());
    }

    [Fact]
    public async Task The_settings_and_audit_schema_matches_the_runtime_model()
    {
        var target = await CreateTargetAsync("settings_audit_schema");
        await MigrateAsync(target);

        var settingsColumns = await ReadColumnFacetsAsync(target, SettingsTable);
        var auditColumns = await ReadColumnFacetsAsync(target, AuditTable);

        Assert.Equal(
            new Dictionary<string, (string Type, bool Nullable, int? MaxLength)>
            {
                ["service_id"] = ("character varying", false, 128),
                ["values_json"] = ("text", false, null),
                ["version"] = ("bigint", false, null),
                ["updated_at_utc"] = ("timestamp with time zone", false, null),
                ["updated_by"] = ("character varying", false, 256),
                ["restart_required"] = ("boolean", false, null),
            },
            settingsColumns);
        Assert.Equal(
            new Dictionary<string, (string Type, bool Nullable, int? MaxLength)>
            {
                ["id"] = ("character varying", false, 36),
                ["operator_id"] = ("character varying", true, 256),
                ["operator_display_name"] = ("character varying", true, 256),
                ["operator_source"] = ("character varying", false, 100),
                ["action"] = ("character varying", false, 200),
                ["target_type"] = ("character varying", false, 200),
                ["target_id"] = ("character varying", false, 256),
                ["outcome"] = ("integer", false, null),
                ["occurred_at_utc"] = ("timestamp with time zone", false, null),
                ["client_ip"] = ("character varying", true, 64),
                ["correlation_id"] = ("character varying", true, 128),
                ["security_description"] = ("character varying", true, 4000),
                ["metadata_json"] = ("character varying", true, 262144),
            },
            auditColumns);

        var constraints = await ReadStringsAsync(
            target,
            """
            SELECT con.conname
            FROM pg_catalog.pg_constraint AS con
            JOIN pg_catalog.pg_class AS class ON class.oid = con.conrelid
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = class.relnamespace
            WHERE namespace.nspname = 'public'
                AND class.relname IN ('service_settings', 'service_audit_logs')
            ORDER BY con.conname
            """);
        Assert.Contains("ck_service_settings_version", constraints);
        // The audit table owns 13 columns and 12 check constraints: the not-empty id check plus 11
        // byte-length checks, exactly the set the public package's mapping declares.
        var auditChecks = constraints.Where(name => name.StartsWith("ck_service_audit_logs_", StringComparison.Ordinal)).ToArray();
        Assert.Equal(12, auditChecks.Length);

        var indexes = await ReadStringsAsync(
            target,
            """
            SELECT indexname
            FROM pg_catalog.pg_indexes
            WHERE schemaname = 'public' AND tablename = 'service_audit_logs'
            ORDER BY indexname
            """);
        var auditIndexes = indexes
            .Where(name => name.StartsWith("ix_service_audit_logs_", StringComparison.Ordinal))
            .ToArray();
        Assert.Equal(6, auditIndexes.Length);
    }

    [Fact]
    public async Task The_settings_and_audit_check_constraints_hold_at_the_database_layer()
    {
        var target = await CreateTargetAsync("settings_audit_constraints");
        await MigrateAsync(target);

        await using var connection = new NpgsqlConnection(target);
        await connection.OpenAsync(Token);
        await using var transaction = await connection.BeginTransactionAsync(Token);

        // Each probe runs against its own savepoint, because a failed PostgreSQL statement aborts
        // the rest of an unprotected transaction (25P02) before its own SQLSTATE can be observed.
        var zeroVersion = connection.CreateCommand();
        zeroVersion.Transaction = transaction;
        zeroVersion.CommandText = """
            INSERT INTO public."service_settings"
                (service_id, values_json, version, updated_at_utc, updated_by, restart_required)
            VALUES ('constraint-probe', '{}', 0, '2026-09-17T00:00:00Z', 'probe', false)
            """;
        await transaction.SaveAsync("settings_probe", Token);
        var zeroVersionFailure = await Assert.ThrowsAsync<PostgresException>(
            () => zeroVersion.ExecuteNonQueryAsync(Token));
        Assert.Equal("23514", zeroVersionFailure.SqlState);
        await transaction.RollbackAsync("settings_probe", Token);

        var emptyId = connection.CreateCommand();
        emptyId.Transaction = transaction;
        emptyId.CommandText = """
            INSERT INTO public."service_audit_logs"
                (id, operator_source, action, target_type, target_id, outcome, occurred_at_utc)
            VALUES ('00000000-0000-0000-0000-000000000000', 'probe', 'probe', 'probe', 'probe', 0, '2026-09-17T00:00:00Z')
            """;
        await transaction.SaveAsync("audit_probe", Token);
        var emptyIdFailure = await Assert.ThrowsAsync<PostgresException>(
            () => emptyId.ExecuteNonQueryAsync(Token));
        Assert.Equal("23514", emptyIdFailure.SqlState);
        await transaction.RollbackAsync("audit_probe", Token);

        // The probe statements all failed, so the rollback leaves both tables empty.
        await transaction.RollbackAsync(Token);
        Assert.Empty(await ReadStringsAsync(target, $"""SELECT service_id FROM public."{SettingsTable}" """));
        Assert.Empty(await ReadStringsAsync(target, $"""SELECT id FROM public."{AuditTable}" """));
    }

    [Fact]
    public async Task The_applied_schema_uses_PostgreSql_storage_types()
    {
        var target = await CreateTargetAsync("storage_types");
        await MigrateAsync(target);

        var columns = await ReadWorkspaceColumnTypesAsync(target);

        Assert.Equal("uuid", columns["Id"]);
        Assert.Equal("character varying", columns["DisplayName"]);
        // The SQLite context's storage types are not what this context produced.
        Assert.DoesNotContain(
            columns.Values,
            type => type.Contains("TEXT", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public async Task A_current_target_is_compatible_and_an_inspection_changes_nothing()
    {
        var target = await CreateTargetAsync("unchanged");
        await MigrateAsync(target);
        var workspace = new ReferenceWorkspace { Id = Guid.NewGuid(), DisplayName = "before" };
        await using (var seed = CreateContext(target))
        {
            seed.Workspaces.Add(workspace);
            await seed.SaveChangesAsync(Token);
        }

        var historyBefore = await ReadHistoryAsync(target);
        var columnsBefore = await ReadWorkspaceColumnTypesAsync(target);
        var rowsBefore = await ReadWorkspaceNamesAsync(target);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        var state = await executor.InspectAsync(Token);

        Assert.Equal(MigrationObservationState.CurrentVersionCompatible, state);
        Assert.Equal(historyBefore, await ReadHistoryAsync(target));
        Assert.Equal(columnsBefore, await ReadWorkspaceColumnTypesAsync(target));
        Assert.Equal(rowsBefore, await ReadWorkspaceNamesAsync(target));
        Assert.Equal(["before"], rowsBefore);
    }

    [Fact]
    public async Task The_caller_owns_the_save_and_the_rollback()
    {
        var target = await CreateTargetAsync("caller_owned_unit_of_work");
        await MigrateAsync(target);
        await using var context = CreateContext(target);

        // Nothing is persisted until the caller saves, and a caller-owned rollback discards it.
        context.Workspaces.Add(new ReferenceWorkspace
        {
            Id = Guid.NewGuid(),
            DisplayName = "rolled-back",
        });
        await using (var rollback = await context.Database.BeginTransactionAsync(Token))
        {
            await context.SaveChangesAsync(Token);
            await rollback.RollbackAsync(Token);
        }

        Assert.Empty(await ReadWorkspaceNamesAsync(target));

        context.ChangeTracker.Clear();
        context.Workspaces.Add(new ReferenceWorkspace
        {
            Id = Guid.NewGuid(),
            DisplayName = "committed",
        });
        await using (var commit = await context.Database.BeginTransactionAsync(Token))
        {
            await context.SaveChangesAsync(Token);
            await commit.CommitAsync(Token);
        }

        Assert.Equal(["committed"], await ReadWorkspaceNamesAsync(target));
    }

    [Fact]
    public async Task A_strict_prefix_of_the_known_set_is_a_pending_migration()
    {
        var target = await CreateTargetAsync("pending");
        await MigrateAsync(target);
        await using var context = CreateContext(target);
        // A test-owned migration set, so the sample keeps only the migrations it has a business
        // need for, and the fully applied pair is a strict prefix of it.
        var executor = new ReferencePostgreSqlMigrationExecutor(
            context,
            target,
            KnownSet(FutureMigration));

        Assert.Equal(MigrationObservationState.PendingMigration, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_unknown_history_record_is_refused_as_a_newer_version()
    {
        var target = await CreateTargetAsync("too_new");
        await MigrateAsync(target);
        await InsertHistoryAsync(target, FutureMigration);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        // The build has nothing left to apply, and that is deliberately not what decides the answer.
        Assert.Empty(await context.Database.GetPendingMigrationsAsync(Token));
        Assert.Equal(MigrationObservationState.VersionTooNew, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task A_gap_in_the_applied_records_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("gap");
        await MigrateAsync(target);
        await InsertHistoryAsync(target, LaterMigration);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(
            context,
            target,
            KnownSet(FutureMigration, LaterMigration));

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_empty_history_over_an_empty_schema_is_adoptable()
    {
        var target = await CreateTargetAsync("empty_history");
        await ExecuteAsync(target, $"""
            CREATE TABLE public."{HistoryTable}" (
                "MigrationId" character varying(150) NOT NULL,
                "ProductVersion" character varying(32) NOT NULL,
                CONSTRAINT "PK___EFMigrationsHistory" PRIMARY KEY ("MigrationId"))
            """);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.Empty, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task Application_relations_without_a_history_are_an_inspection_failure()
    {
        var target = await CreateTargetAsync("no_history");
        await ExecuteAsync(target, """CREATE TABLE public."someone_elses_table" ("Id" integer)""");
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task A_missing_workspace_table_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("missing_table");
        await MigrateAsync(target);
        await ExecuteAsync(target, $"""DROP TABLE public."{WorkspaceTable}" """);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task A_missing_workspace_column_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("missing_column");
        await MigrateAsync(target);
        await ExecuteAsync(target, $"""ALTER TABLE public."{WorkspaceTable}" DROP COLUMN "DisplayName" """);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task A_missing_installation_table_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("missing_installation_table");
        await MigrateAsync(target);
        await ExecuteAsync(target, $"""DROP TABLE public."{InstallationTable}" """);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        // The history claims the current version, but the installation table is part of that
        // version's schema, so its absence is not compatible and not repaired either.
        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Theory]
    [MemberData(nameof(InstallationColumns))]
    public async Task A_missing_installation_column_is_an_inspection_failure(string column)
    {
        var target = await CreateTargetAsync($"missing_installation_{column}");
        await MigrateAsync(target);
        await ExecuteAsync(target, $"""ALTER TABLE public."{InstallationTable}" DROP COLUMN "{column}" """);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_unreadable_history_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("unreadable_history");
        await ExecuteAsync(
            target,
            $"""CREATE TABLE public."{HistoryTable}" ("Unexpected" text NOT NULL)""");
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_unsupported_relation_kind_in_the_owned_schema_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("unsupported_relation");
        await MigrateAsync(target);
        await ExecuteAsync(
            target,
            $"""CREATE VIEW public."workspace_view" AS SELECT "Id" FROM public."{WorkspaceTable}" """);
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task An_application_relation_in_another_schema_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("other_schema");
        await MigrateAsync(target);
        await ExecuteAsync(target, """CREATE SCHEMA "not_ours" """);
        await ExecuteAsync(target, """CREATE TABLE "not_ours"."theirs" ("Id" integer)""");
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
    }

    [Fact]
    public async Task A_missing_database_is_an_inspection_failure_and_is_not_created()
    {
        var target = await CreateTargetAsync("absent");
        await DropTargetAsync("absent");
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        var state = await executor.InspectAsync(Token);

        Assert.Equal(MigrationObservationState.InspectionFailed, state);
        Assert.False(await DatabaseExistsAsync("absent"));
    }

    [Fact]
    public async Task An_unreachable_server_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("unreachable");
        var unreachable = new NpgsqlConnectionStringBuilder(target)
        {
            // A port nothing listens on, so the failure is the connection itself.
            Port = 1,
            Timeout = 2,
        }.ConnectionString;
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, unreachable);

        var state = await executor.InspectAsync(Token);

        // The observation's only output is the finite state itself: the provider's own message and
        // the connection it failed on have nowhere to travel through this result.
        Assert.Equal(MigrationObservationState.InspectionFailed, state);
    }

    [Fact]
    public async Task A_read_permission_failure_is_an_inspection_failure()
    {
        var target = await CreateTargetAsync("unauthorized_read");
        await MigrateAsync(target);
        await CreateStrangerRoleAsync();
        var stranger = new NpgsqlConnectionStringBuilder(target)
        {
            Username = SyntheticReaderRole,
            Password = SyntheticReaderPassword,
        }.ConnectionString;
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, stranger);

        // The role can read the catalog but was granted nothing on the tables themselves.
        Assert.Equal(MigrationObservationState.InspectionFailed, await executor.InspectAsync(Token));
        Assert.Equal(KnownSet(), await ReadHistoryAsync(target));
    }

    [Fact]
    public async Task A_pre_cancelled_call_keeps_the_original_token_and_touches_nothing()
    {
        var target = await CreateTargetAsync("pre_cancelled");
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();

        var inspection = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.InspectAsync(abort.Token));
        var execution = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, inspection.CancellationToken);
        Assert.Equal(abort.Token, execution.CancellationToken);
        // Nothing was observed, created, or repaired.
        Assert.Empty(await ReadRelationNamesAsync(target));
    }

    [Fact]
    public async Task Cancelling_during_execution_keeps_the_original_token()
    {
        var target = await CreateTargetAsync("cancelled_execution");
        using var abort = new CancellationTokenSource();
        await using var context = CreateContext(target, new CancelOnFirstCommandInterceptor(abort));
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        var failure = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await executor.ExecuteAsync(abort.Token));

        Assert.Equal(abort.Token, failure.CancellationToken);
        Assert.IsNotType<ReferencePostgreSqlMigrationFailedException>(failure);
        Assert.True(abort.IsCancellationRequested);
    }

    [Fact]
    public async Task An_execution_failure_is_a_fixed_safe_exception_and_not_a_cancellation()
    {
        var target = await CreateTargetAsync("execution_failure");
        // The migration's own table already exists under a foreign definition, so applying it fails.
        await ExecuteAsync(target, $"""CREATE TABLE public."{WorkspaceTable}" ("Id" integer)""");
        await using var context = CreateContext(target);
        var executor = new ReferencePostgreSqlMigrationExecutor(context, target);

        var failure = await Assert.ThrowsAsync<ReferencePostgreSqlMigrationFailedException>(async () =>
            await executor.ExecuteAsync(Token));

        Assert.Null(failure.InnerException);
        Assert.Equal(
            "The reference service PostgreSQL schema migration did not complete.",
            failure.Message);
        var text = failure.ToString();
        Assert.DoesNotContain(SyntheticPassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticUser, text, StringComparison.Ordinal);
        // Neither the provider's own message nor its SQL state reaches the caller through this.
        Assert.DoesNotContain("already exists", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("42P07", text, StringComparison.Ordinal);
    }

    private ReferencePostgreSqlDbContext CreateContext(
        string connectionString,
        IInterceptor? interceptor = null)
    {
        var builder = new DbContextOptionsBuilder<ReferencePostgreSqlDbContext>()
            .UseNpgsql(connectionString);
        if (interceptor is not null)
        {
            builder = builder.AddInterceptors(interceptor);
        }

        return new ReferencePostgreSqlDbContext(builder.Options);
    }

    private async Task MigrateAsync(string connectionString)
    {
        await using var context = CreateContext(connectionString);
        await context.Database.MigrateAsync(Token);
    }

    private async Task<string> CreateTargetAsync(string name)
    {
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql,
            maintenanceConnectionString is not null);
        var database = DatabaseName(name);
        await ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{database}" WITH (FORCE)""");
        await ExecuteOnMaintenanceAsync($"""CREATE DATABASE "{database}" """);
        return Target(database);
    }

    private Task DropTargetAsync(string name) =>
        ExecuteOnMaintenanceAsync($"""DROP DATABASE IF EXISTS "{DatabaseName(name)}" WITH (FORCE)""");

    private async Task<bool> DatabaseExistsAsync(string name)
    {
        await using var connection = new NpgsqlConnection(maintenanceConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT 1 FROM pg_catalog.pg_database WHERE datname = $1";
        command.Parameters.AddWithValue(DatabaseName(name));
        return await command.ExecuteScalarAsync(Token) is not null;
    }

    private async Task CreateStrangerRoleAsync()
    {
        // A login role that was granted nothing beyond the catalog every role can read.
        await ExecuteOnMaintenanceAsync($"""
            DO $$
            BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_catalog.pg_roles WHERE rolname = '{SyntheticReaderRole}') THEN
                    CREATE ROLE "{SyntheticReaderRole}" LOGIN PASSWORD '{SyntheticReaderPassword}';
                END IF;
            END
            $$
            """);
    }

    private string Target(string database) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
            // The finite result must not depend on the provider volunteering extra error text.
            IncludeErrorDetail = false,
        }.ConnectionString;

    private static string DatabaseName(string name) => $"reference_workspace_{name}";

    private Task ExecuteOnMaintenanceAsync(string statement) =>
        ExecuteAsync(maintenanceConnectionString!, statement);

    private async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private Task InsertHistoryAsync(string connectionString, string migrationId) =>
        ExecuteAsync(
            connectionString,
            $"""
            INSERT INTO public."{HistoryTable}" ("MigrationId", "ProductVersion")
            VALUES ('{migrationId}', '10.0.0')
            """);

    private Task<List<string>> ReadHistoryAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            $"""SELECT "MigrationId" FROM public."{HistoryTable}" ORDER BY "MigrationId" """);

    private Task<List<string>> ReadWorkspaceNamesAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            $"""SELECT "DisplayName" FROM public."{WorkspaceTable}" ORDER BY "DisplayName" """);

    private Task<List<string>> ReadInstallationServiceIdsAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            $"""SELECT service_id FROM public."{InstallationTable}" ORDER BY service_id """);

    private Task<List<string>> ReadRelationNamesAsync(string connectionString) =>
        ReadStringsAsync(
            connectionString,
            """
            SELECT class.relname
            FROM pg_catalog.pg_class AS class
            JOIN pg_catalog.pg_namespace AS namespace ON namespace.oid = class.relnamespace
            WHERE namespace.nspname = 'public'
            ORDER BY class.relname
            """);

    private async Task<List<string>> ReadStringsAsync(string connectionString, string query)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = query;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.GetString(0));
        }

        return values;
    }

    private async Task<Dictionary<string, (string Type, bool Nullable, int? MaxLength)>> ReadColumnFacetsAsync(
        string connectionString,
        string tableName)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, data_type, is_nullable, character_maximum_length
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = $1
            ORDER BY ordinal_position
            """;
        command.Parameters.AddWithValue(tableName);
        var columns = new Dictionary<string, (string Type, bool Nullable, int? MaxLength)>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns[reader.GetString(0)] = (
                reader.GetString(1),
                string.Equals(reader.GetString(2), "YES", StringComparison.Ordinal),
                reader.IsDBNull(3) ? null : reader.GetInt32(3));
        }

        return columns;
    }

    private async Task<Dictionary<string, string>> ReadWorkspaceColumnTypesAsync(string connectionString)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT column_name, data_type
            FROM information_schema.columns
            WHERE table_schema = 'public' AND table_name = $1
            """;
        command.Parameters.AddWithValue(WorkspaceTable);
        var columns = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            columns[reader.GetString(0)] = reader.GetString(1);
        }

        return columns;
    }

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    /// <summary>
    /// Cancels the caller's own source as the migration reaches its first statement, so the
    /// cancellation lands inside the execution rather than before it.
    /// </summary>
    private sealed class CancelOnFirstCommandInterceptor(CancellationTokenSource source)
        : DbCommandInterceptor
    {
        public override async ValueTask<InterceptionResult<int>> NonQueryExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<int> result,
            CancellationToken cancellationToken = default)
        {
            await source.CancelAsync();
            return result;
        }

        public override async ValueTask<InterceptionResult<DbDataReader>> ReaderExecutingAsync(
            DbCommand command,
            CommandEventData eventData,
            InterceptionResult<DbDataReader> result,
            CancellationToken cancellationToken = default)
        {
            await source.CancelAsync();
            return result;
        }
    }
}
