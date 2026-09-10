using System.Net;
using Microsoft.Data.Sqlite;
using ServiceMantle.ReferenceService.Database.Sqlite;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Accepts the sample's explicit single-instance SQLite deployment and its refusal of every other
/// declared mode, from outside the process.
/// </summary>
/// <remarks>
/// <para>
/// Every case here starts the reference service's own build output as a real operating-system
/// process with a real Kestrel on a loopback dynamic port. Nothing is substituted: no test server,
/// no replacement provider, no replacement migration executor, and no pre-created schema standing
/// in for a first start. The evidence is an HTTP response, a process exit code, the host's own
/// console output, read-only SQLite queries, and the files the process left behind.
/// </para>
/// <para>
/// The narrower unit and in-process coverage stays where it is. This file owns only what a separate
/// process can show, and it deliberately proves nothing beyond it: two processes that both declare
/// SingleInstance are not detected here, a completed start is schema readiness rather than a
/// completed service setup, and an interrupted migration is not claimed to roll back.
/// </para>
/// </remarks>
public sealed class ReferenceSqliteDeploymentEndToEndTests
{
    private const string WorkspaceMigration = "20260903000000_InitialReferenceWorkspace";
    private const string HistoryTable = "__EFMigrationsHistory";
    private const string WorkspaceTable = "reference_workspaces";
    private const string FutureMigration = "20260904000000_FutureReferenceStep";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_gate_is_off_by_default_and_the_real_process_serves_without_a_database()
    {
        using var directory = TemporaryDirectory.Create();
        await using var service = ReferenceServiceProcess.Start(
            directory.Path,
            "--" + ReferenceSqliteStartupOptions.DatabasePathKey,
            directory.DatabasePath);

        var address = await service.WaitUntilListeningAsync(Token);
        var response = await GetRootAsync(address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("skeleton", response.Body, StringComparison.Ordinal);
        // The switch is off, so no file was created - not even an empty one, and not by the
        // registered context, whose connection string points at this path.
        Assert.False(File.Exists(directory.DatabasePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));

        await StopAndAssertItWentAwayAsync(service);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task An_authorized_start_migrates_the_missing_target_before_it_serves()
    {
        using var directory = TemporaryDirectory.Create();
        await using var service = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: true));

        var address = await service.WaitUntilListeningAsync(Token);

        // Read before the first request: the gate completes in the host's starting phase, so the
        // workspace schema is already there when Kestrel reports its address.
        Assert.True(File.Exists(directory.DatabasePath));
        Assert.Equal([WorkspaceMigration], await ReadHistoryAsync(directory));
        Assert.Contains(WorkspaceTable, await ReadTableNamesAsync(directory));
        // A completed start is a migrated schema, not a completed service setup: the contributor
        // that would register the first workspace never ran.
        Assert.Equal(0, await CountWorkspacesAsync(directory));

        var response = await GetRootAsync(address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("skeleton", response.Body, StringComparison.Ordinal);
        await StopAndAssertItWentAwayAsync(service);
        Assert.Empty(SidecarFiles(directory));
    }

    [Fact]
    public async Task A_restart_over_the_same_target_serves_again_without_applying_a_migration()
    {
        using var directory = TemporaryDirectory.Create();
        await using (var first = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: true)))
        {
            await first.WaitUntilListeningAsync(Token);
            await StopAndAssertItWentAwayAsync(first);
        }

        // The schema came from a real first start above. This row is the pre-existing data the
        // restart must leave alone; it is written after the first process is gone.
        var workspace = Guid.NewGuid();
        await InsertWorkspaceAsync(directory, workspace, "restart-owned");
        var before = await File.ReadAllBytesAsync(directory.DatabasePath, Token);

        await using var second = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: false));

        var address = await second.WaitUntilListeningAsync(Token);
        var response = await GetRootAsync(address);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([WorkspaceMigration], await ReadHistoryAsync(directory));
        Assert.Equal([(workspace, "restart-owned")], await ReadWorkspacesAsync(directory));
        Assert.Equal(before, await File.ReadAllBytesAsync(directory.DatabasePath, Token));
        Assert.Empty(SidecarFiles(directory));
        await StopAndAssertItWentAwayAsync(second);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData(null, true)]
    [InlineData("", false)]
    [InlineData("", true)]
    [InlineData("Unspecified", false)]
    [InlineData("Unspecified", true)]
    [InlineData("MultiInstance", false)]
    [InlineData("MultiInstance", true)]
    [InlineData("3", false)]
    [InlineData("3", true)]
    [InlineData("not-a-mode", false)]
    [InlineData("not-a-mode", true)]
    public async Task An_unauthorized_deployment_mode_ends_the_process_before_it_listens(
        string? mode,
        bool targetExists)
    {
        using var directory = TemporaryDirectory.Create();
        var before = targetExists ? await CreateExistingTargetAsync(directory) : null;
        var arguments = new List<string>
        {
            "--" + ReferenceSqliteStartupOptions.DatabasePathKey, directory.DatabasePath,
            "--" + ReferenceSqliteStartupOptions.EnabledKey, "true",
            "--" + ReferenceSqliteStartupOptions.PrepareIfMissingKey, "true",
        };
        if (mode is not null)
        {
            arguments.AddRange(["--" + ReferenceSqliteStartupOptions.DeploymentModeKey, mode]);
        }

        await using var service = ReferenceServiceProcess.Start(directory.Path, [.. arguments]);
        var exitCode = await service.WaitForExitAsync(Token);

        Assert.NotEqual(0, exitCode);
        AssertItNeverListened(service);
        Assert.Contains(
            ReferenceSqliteStartupOptions.DeploymentModeKey,
            service.Output,
            StringComparison.Ordinal);
        AssertTargetIsUntouched(directory, before);
    }

    [Fact]
    public async Task A_missing_target_ends_the_process_when_preparation_was_not_authorized()
    {
        using var directory = TemporaryDirectory.Create();
        await using var service = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: false));

        var exitCode = await service.WaitForExitAsync(Token);

        Assert.NotEqual(0, exitCode);
        AssertItNeverListened(service);
        AssertFiniteOutcome(service, ReferenceSqliteStartupOutcome.TargetMissing);
        AssertTargetIsUntouched(directory, before: null);
    }

    [Fact]
    public async Task An_unusable_existing_target_is_never_repaired_or_replaced()
    {
        using var directory = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(directory.DatabasePath, "not a database at all", Token);
        var before = await File.ReadAllBytesAsync(directory.DatabasePath, Token);

        await using var service = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: true));

        var exitCode = await service.WaitForExitAsync(Token);

        Assert.NotEqual(0, exitCode);
        AssertItNeverListened(service);
        AssertFiniteOutcome(service);
        AssertTargetIsUntouched(directory, before);
    }

    [Fact]
    public async Task A_history_this_build_does_not_know_ends_the_process_without_touching_it()
    {
        using var directory = TemporaryDirectory.Create();
        // The unknown record is written by this fixture, into its own database, while nothing is
        // running: a real start never produces one.
        await using (var first = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: true)))
        {
            await first.WaitUntilListeningAsync(Token);
            await StopAndAssertItWentAwayAsync(first);
        }

        await ExecuteAsync(
            directory,
            $"""INSERT INTO "{HistoryTable}" ("MigrationId", "ProductVersion") """ +
                $"VALUES ('{FutureMigration}', '10.0.0')");
        var before = await File.ReadAllBytesAsync(directory.DatabasePath, Token);

        await using var second = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: false));

        var exitCode = await second.WaitForExitAsync(Token);

        Assert.NotEqual(0, exitCode);
        AssertItNeverListened(second);
        AssertFiniteOutcome(second, ReferenceSqliteStartupOutcome.MigrationFailed);
        Assert.Equal([WorkspaceMigration, FutureMigration], await ReadHistoryAsync(directory));
        AssertTargetIsUntouched(directory, before);
    }

    [Fact]
    public async Task Stopping_the_running_host_ends_the_process_and_releases_the_target()
    {
        using var directory = TemporaryDirectory.Create();
        await using var service = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: true));
        await service.WaitUntilListeningAsync(Token);

        await StopAndAssertItWentAwayAsync(service);

        // Nothing is holding the file any more: it can be taken exclusively and removed.
        using (var exclusive = new FileStream(
            directory.DatabasePath,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None))
        {
            Assert.True(exclusive.Length > 0);
        }

        Assert.Empty(SidecarFiles(directory));
        File.Delete(directory.DatabasePath);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task The_process_never_reports_the_target_path_or_a_connection_string()
    {
        using var directory = TemporaryDirectory.Create();
        await using var service = ReferenceServiceProcess.Start(
            directory.Path,
            AuthorizedArguments(directory, prepareIfMissing: true));

        var address = await service.WaitUntilListeningAsync(Token);
        var response = await GetRootAsync(address);
        await StopAndAssertItWentAwayAsync(service);

        // Only what this sample's own start classification, exception messages and HTTP output
        // carry is asserted here; no claim is made about third-party or framework diagnostics.
        foreach (var text in new[] { service.Output, response.Body })
        {
            Assert.DoesNotContain(directory.DatabasePath, text, StringComparison.Ordinal);
            Assert.DoesNotContain("Data Source", text, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Mode=ReadWrite", text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void The_started_build_output_carries_only_the_dependencies_this_sample_declares()
    {
        var files = Directory
            .EnumerateFiles(ReferenceServiceBuildOutput.Directory, "*", SearchOption.AllDirectories)
            .Select(Path.GetFileName)
            .Select(name => name!)
            .ToList();

        // The sample's own declared set, so the acceptance runs the assemblies it claims to.
        foreach (var expected in new[]
        {
            "ServiceMantle.dll",
            "ServiceMantle.AspNetCore.dll",
            "ServiceMantle.Serilog.dll",
            "ServiceMantle.Database.Sqlite.dll",
            "Microsoft.EntityFrameworkCore.Sqlite.dll",
            "Microsoft.Data.Sqlite.dll",
            // The sample also declares the PostgreSQL EF provider for its separate PostgreSQL
            // context. No host path here uses it, and it brings no ServiceMantle provider package.
            "Npgsql.EntityFrameworkCore.PostgreSQL.dll",
            "Npgsql.dll",
        })
        {
            Assert.Contains(expected, files, StringComparer.Ordinal);
        }

        // No other database provider is on this path, so nothing here can be served by one.
        foreach (var forbidden in new[]
        {
            "ServiceMantle.Database.PostgreSql",
            "ServiceMantle.Database.MySql",
            "ServiceMantle.Database.MariaDb",
            "ServiceMantle.Database.Oracle",
            "ServiceMantle.Database.SqlServer",
            "MySqlConnector",
            "Pomelo",
            "Oracle.ManagedDataAccess",
            "Microsoft.Data.SqlClient",
        })
        {
            Assert.DoesNotContain(
                files,
                name => name.StartsWith(forbidden, StringComparison.OrdinalIgnoreCase));
        }
    }

    private static string[] AuthorizedArguments(TemporaryDirectory directory, bool prepareIfMissing) =>
    [
        "--" + ReferenceSqliteStartupOptions.DatabasePathKey, directory.DatabasePath,
        "--" + ReferenceSqliteStartupOptions.EnabledKey, "true",
        "--" + ReferenceSqliteStartupOptions.DeploymentModeKey, "SingleInstance",
        "--" + ReferenceSqliteStartupOptions.PrepareIfMissingKey,
        prepareIfMissing ? "true" : "false",
    ];

    private static async Task StopAndAssertItWentAwayAsync(ReferenceServiceProcess service)
    {
        var exitCode = await service.ShutDownAsync(Token);

        Assert.True(service.HasExited);
        if (ReferenceServiceProcess.SupportsGracefulShutdownSignal)
        {
            // Where an operator's stop request can be delivered, the host owns its own shutdown.
            Assert.Equal(0, exitCode);
        }
    }

    private static void AssertItNeverListened(ReferenceServiceProcess service)
    {
        Assert.True(service.HasExited);
        Assert.DoesNotContain("Now listening on", service.Output, StringComparison.Ordinal);
        Assert.DoesNotContain("Application started", service.Output, StringComparison.Ordinal);
    }

    private static void AssertFiniteOutcome(
        ReferenceServiceProcess service,
        ReferenceSqliteStartupOutcome? expected = null)
    {
        Assert.Contains(
            "The reference SQLite startup deployment did not complete",
            service.Output,
            StringComparison.Ordinal);
        if (expected is { } outcome)
        {
            Assert.Contains(outcome.ToString(), service.Output, StringComparison.Ordinal);
            return;
        }

        Assert.Contains(
            Enum.GetValues<ReferenceSqliteStartupOutcome>()
                .Where(candidate => candidate != ReferenceSqliteStartupOutcome.Ready),
            candidate => service.Output.Contains(candidate.ToString(), StringComparison.Ordinal));
    }

    private static void AssertTargetIsUntouched(TemporaryDirectory directory, byte[]? before)
    {
        Assert.Empty(SidecarFiles(directory));
        if (before is null)
        {
            Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
            return;
        }

        Assert.Equal([directory.DatabasePath], Directory.GetFileSystemEntries(directory.Path));
        Assert.Equal(before, File.ReadAllBytes(directory.DatabasePath));
    }

    private static string[] SidecarFiles(TemporaryDirectory directory) =>
        Directory.GetFiles(directory.Path, "reference.db-*");

    private static async Task<byte[]> CreateExistingTargetAsync(TemporaryDirectory directory)
    {
        await ReferenceSqliteStartupTests.CreateDatabaseAsync(
            directory,
            $"""CREATE TABLE "{WorkspaceTable}" ("Id" TEXT NOT NULL PRIMARY KEY)""");
        return await File.ReadAllBytesAsync(directory.DatabasePath, Token);
    }

    private static async Task<(HttpStatusCode StatusCode, string Body)> GetRootAsync(Uri address)
    {
        using var client = new HttpClient { BaseAddress = address, Timeout = ReferenceServiceBudgets.Request };
        using var response = await client.GetAsync("/", Token);
        return (response.StatusCode, await response.Content.ReadAsStringAsync(Token));
    }

    private static Task<List<string>> ReadHistoryAsync(TemporaryDirectory directory) => ReadAsync(
        directory,
        $"""SELECT "MigrationId" FROM "{HistoryTable}" ORDER BY "MigrationId" """,
        reader => reader.GetString(0));

    private static Task<List<string>> ReadTableNamesAsync(TemporaryDirectory directory) => ReadAsync(
        directory,
        "SELECT name FROM sqlite_schema WHERE type = 'table' AND name NOT LIKE 'sqlite_%'",
        reader => reader.GetString(0));

    private static async Task<int> CountWorkspacesAsync(TemporaryDirectory directory) =>
        (await ReadAsync(
            directory,
            $"""SELECT COUNT(*) FROM "{WorkspaceTable}" """,
            reader => reader.GetInt32(0)))[0];

    private static Task<List<(Guid Id, string DisplayName)>> ReadWorkspacesAsync(
        TemporaryDirectory directory) => ReadAsync(
            directory,
            $"""SELECT "Id", "DisplayName" FROM "{WorkspaceTable}" ORDER BY "Id" """,
            reader => (reader.GetGuid(0), reader.GetString(1)));

    /// <summary>Reads the target through its own read-only connection; it creates and writes nothing.</summary>
    private static async Task<List<T>> ReadAsync<T>(
        TemporaryDirectory directory,
        string sql,
        Func<SqliteDataReader, T> read)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath,
            Mode = SqliteOpenMode.ReadOnly,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var rows = new List<T>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            rows.Add(read((SqliteDataReader)reader));
        }

        return rows;
    }

    private static async Task ExecuteAsync(TemporaryDirectory directory, string statement)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static Task InsertWorkspaceAsync(
        TemporaryDirectory directory,
        Guid id,
        string displayName) => ExecuteAsync(
            directory,
            $"""INSERT INTO "{WorkspaceTable}" ("Id", "DisplayName") """ +
                $"VALUES ('{id}', '{displayName}')");
}
