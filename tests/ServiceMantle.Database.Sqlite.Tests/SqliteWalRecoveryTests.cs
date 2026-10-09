using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.Data.Sqlite;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace ServiceMantle.Database.Sqlite.Tests;

public sealed class SqliteWalRecoveryTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    [Fact(Explicit = true)]
    public void Crash_writer_fixture()
    {
        var path = Environment.GetEnvironmentVariable("SERVICEMANTLE_SQLITE_CRASH_PATH");
        if (path is null) return;
        // An isolated child exits without disposing SQLite, preserving committed WAL pages.
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA wal_autocheckpoint=0; CREATE TABLE proof(value INTEGER); INSERT INTO proof VALUES (42);";
        command.ExecuteNonQuery();
        Environment.Exit(0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Real_crash_sidecars_are_recovered_only_when_explicitly_enabled(bool prepareEntry)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "crash #100%.db");
        await CrashAsync(path);
        var target = Target(path);
        var disabled = await new SqliteDatabaseTargetPreparationProvider().ObserveAsync(target, Token);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, disabled.ErrorCode);
        var provider = Enabled();
        if (prepareEntry)
        {
            var prepared = await provider.PrepareAsync(DatabaseTargetPreparationRequest.ForFile(target), Budget, Token);
            Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, prepared.Outcome);
        }
        else Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable, (await provider.ObserveAsync(target, Token)).Status);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM proof";
        Assert.Equal(42L, await command.ExecuteScalarAsync(Token));
    }

    [Fact]
    public async Task Publish_winner_uses_the_same_single_recovery_observation()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "winner.db");
        var access = new CountingAccess(new SqliteDatabaseAccess());
        var provider = new SqliteDatabaseTargetPreparationProvider(new SqliteTargetFileSystem(), access,
            async (checkpoint, _) => { if (checkpoint == SqlitePreparationCheckpoint.BeforePublish) await CrashAsync(path); }, new(true, Budget));
        var prepared = await provider.PrepareAsync(DatabaseTargetPreparationRequest.ForFile(Target(path)), Budget, Token);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, prepared.Outcome);
        Assert.Equal(1, access.Recoveries);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData("observe")]
    [InlineData("prepare")]
    public async Task Clean_WAL_without_sidecars_is_refused_by_default_and_admitted_only_with_opt_in(string entry)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "clean.db");
        await CreateCleanWalAsync(path);
        var target = Target(path);

        var disabled = await new SqliteDatabaseTargetPreparationProvider().ObserveAsync(target, Token);
        Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, disabled.Status);
        Assert.True(disabled.TargetExists);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, disabled.ErrorCode);

        var access = new CountingAccess(new SqliteDatabaseAccess());
        var provider = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(), access, recoveryOptions: new(true, Budget));
        if (entry == "prepare")
        {
            var prepared = await provider.PrepareAsync(DatabaseTargetPreparationRequest.ForFile(target), Budget, Token);
            Assert.True(prepared.Succeeded);
            Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, prepared.Outcome);
        }
        else
        {
            var observed = await provider.ObserveAsync(target, Token);
            Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable, observed.Status);
        }

        Assert.Equal(1, access.Recoveries);
        Assert.Equal(1, access.ImmutableObservations);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
        var header = await File.ReadAllBytesAsync(path, Token);
        Assert.Equal((byte)2, header[18]);
        Assert.Equal((byte)2, header[19]);
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA user_version";
            Assert.Equal(42L, await command.ExecuteScalarAsync(Token));
            command.CommandText = "SELECT count(*) FROM retained";
            Assert.Equal(1L, await command.ExecuteScalarAsync(Token));
        }
    }

    [Fact]
    public async Task Clean_WAL_intake_is_idempotent_without_sidecar_residue()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "idempotent.db");
        await CreateCleanWalAsync(path);
        var access = new CountingAccess(new SqliteDatabaseAccess());
        var provider = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(), access, recoveryOptions: new(true, Budget));

        var first = await provider.ObserveAsync(Target(path), Token);
        var second = await provider.ObserveAsync(Target(path), Token);

        Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable, first.Status);
        Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable, second.Status);
        Assert.Equal(2, access.Recoveries);
        Assert.Equal(2, access.ImmutableObservations);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
        var header = await File.ReadAllBytesAsync(path, Token);
        Assert.Equal((byte)2, header[18]);
        Assert.Equal((byte)2, header[19]);
    }

    [Fact]
    public async Task Clean_WAL_eligibility_failure_fails_closed_without_a_read_write_open()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "ineligible.db");
        await CreateCleanWalAsync(path);
        UnixFileMode? mode = null;
        FileAttributes? attributes = null;
        if (OperatingSystem.IsWindows())
        {
            attributes = File.GetAttributes(path);
            File.SetAttributes(path, FileAttributes.ReadOnly);
        }
        else
        {
            mode = File.GetUnixFileMode(path);
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.GroupRead | UnixFileMode.OtherRead);
        }

        try
        {
            var access = new CountingAccess(new SqliteDatabaseAccess());
            var provider = new SqliteDatabaseTargetPreparationProvider(
                new SqliteTargetFileSystem(), access, recoveryOptions: new(true));
            var observed = await provider.ObserveAsync(Target(path), Token);

            Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, observed.Status);
            Assert.True(observed.TargetExists);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, observed.ErrorCode);
            Assert.Equal(0, access.Recoveries);
            Assert.Equal(0, access.ImmutableObservations);
            Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
        }
        finally
        {
            if (OperatingSystem.IsWindows()) File.SetAttributes(path, attributes!.Value);
            else File.SetUnixFileMode(path, mode!.Value);
        }
    }

    [Fact]
    public async Task Publish_winner_with_a_clean_WAL_database_uses_the_opt_in_intake()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "winner-clean.db");
        var access = new CountingAccess(new SqliteDatabaseAccess());
        var provider = new SqliteDatabaseTargetPreparationProvider(new SqliteTargetFileSystem(), access,
            async (checkpoint, _) => { if (checkpoint == SqlitePreparationCheckpoint.BeforePublish) await CreateCleanWalAsync(path); },
            new(true, Budget));
        var prepared = await provider.PrepareAsync(DatabaseTargetPreparationRequest.ForFile(Target(path)), Budget, Token);

        Assert.True(prepared.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, prepared.Outcome);
        Assert.Equal(1, access.Recoveries);
        Assert.Equal(1, access.ImmutableObservations);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Clean_WAL_opt_in_passes_bootstrap_validation_through_the_shared_observer()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "bootstrap-clean.db");
        await CreateCleanWalAsync(path);
        var observer = new SqliteDatabaseTargetPreparationProvider(new SqliteTargetRecoveryOptions(true, Budget));

        var result = await new SqliteBootstrapDatabaseProvider(observer).ValidateAsync(Target(path), Token);

        Assert.True(result.IsValid);
        Assert.Null(result.ErrorCode);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Clean_WAL_intake_gates_on_header_eligibility_and_recovery_result()
    {
        // A header the access layer does not classify as clean WAL keeps the enabled observer on
        // the default read-only classification path.
        var filesystem = new CleanWalFileSystem();
        var access = new ScriptedAccess();
        var provider = new SqliteDatabaseTargetPreparationProvider(filesystem, access, recoveryOptions: new(true));
        var observed = await provider.ObserveAsync(Target(AbsolutePath), Token);
        Assert.Null(observed.ErrorCode);
        Assert.Equal(1, access.CleanWalChecks);
        Assert.Equal(1, access.Inspections);
        Assert.Equal(0, access.Recoveries);

        // A clean WAL header that fails eligibility validation never opens a read-write connection.
        access = new ScriptedAccess { CleanWal = true };
        provider = new(new CleanWalFileSystem { Eligible = false }, access, recoveryOptions: new(true));
        observed = await provider.ObserveAsync(Target(AbsolutePath), Token);
        Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, observed.Status);
        Assert.True(observed.TargetExists);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, observed.ErrorCode);
        Assert.Equal(0, access.Recoveries);
        Assert.Equal(0, access.Inspections);

        // A busy checkpoint fails closed as a conflict after exactly one attempt.
        access = new ScriptedAccess { CleanWal = true, RecoveryResult = SqliteRecoveryStatus.Busy };
        provider = new(new CleanWalFileSystem(), access, recoveryOptions: new(true));
        observed = await provider.ObserveAsync(Target(AbsolutePath), Token);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, observed.ErrorCode);
        Assert.Equal(1, access.Recoveries);
        Assert.Equal(0, access.Inspections);

        // A failed checkpoint fails closed as a preparation failure.
        access = new ScriptedAccess { CleanWal = true, RecoveryResult = SqliteRecoveryStatus.Failed };
        provider = new(new CleanWalFileSystem(), access, recoveryOptions: new(true));
        observed = await provider.ObserveAsync(Target(AbsolutePath), Token);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, observed.ErrorCode);
        Assert.Equal(1, access.Recoveries);

        // A checkpoint that exceeds the recovery budget fails closed as a bounded timeout.
        access = new ScriptedAccess { CleanWal = true, Delay = true };
        provider = new(new CleanWalFileSystem(), access, recoveryOptions: new(true, TimeSpan.FromMilliseconds(20)));
        observed = await provider.ObserveAsync(Target(AbsolutePath), Token);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, observed.ErrorCode);
        Assert.Equal(1, access.Recoveries);
    }

    [Theory]
    [InlineData("classify")]
    [InlineData("eligibility")]
    public async Task Clean_WAL_intake_cancellation_keeps_the_caller_token_without_recovery(string stage)
    {
        using var cts = new CancellationTokenSource();
        var filesystem = new CleanWalFileSystem { Eligibility = stage == "eligibility" ? () => cts.Cancel() : null };
        var access = new ScriptedAccess
        {
            CleanWal = true,
            CleanWalCheck = stage == "classify" ? () => cts.Cancel() : null
        };
        var provider = new SqliteDatabaseTargetPreparationProvider(filesystem, access, recoveryOptions: new(true));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.ObserveAsync(Target(AbsolutePath), cts.Token).AsTask());

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.Equal(0, access.Recoveries);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Real_clean_WAL_cancellation_releases_owned_resources_and_leaves_no_partial_state(int stage)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "cancel-clean.db");
        await CreateCleanWalAsync(path);
        using var cts = new CancellationTokenSource();
        var released = false;
        var access = new SqliteDatabaseAccess((checkpoint, state, _) =>
        {
            if (checkpoint == SqliteRecoveryCheckpoint.ResourcesReleased)
            {
                Assert.Equal(System.Data.ConnectionState.Closed, state);
                released = true;
            }

            if ((int)checkpoint == stage) cts.Cancel();
            return ValueTask.CompletedTask;
        });
        var provider = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(), access, recoveryOptions: new(true));

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.ObserveAsync(Target(path), cts.Token).AsTask());

        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.True(released);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
        var header = await File.ReadAllBytesAsync(path, Token);
        Assert.Equal((byte)2, header[18]);
        Assert.Equal((byte)2, header[19]);
        // FileShare.None detects a leaked owned handle on Windows; Unix relies on the lifecycle hook.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Gate_with_explicit_provider_instance_recovers_before_executor_runs()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "gate.db");
        await CrashAsync(path);
        var provider = Enabled();
        var services = new ServiceCollection();
        services.AddSingleton<IDatabaseMigrationExecutor>(new Executor());
        using var container = services.BuildServiceProvider();
        var gate = new StartupDatabaseGate(new([provider], DatabaseProviderIdResolver.Empty),
            new([provider], DatabaseProviderIdResolver.Empty), new([], DatabaseProviderIdResolver.Empty),
            container.GetRequiredService<IServiceScopeFactory>());
        var result = await gate.RunAsync(new(Target(path), DatabaseDeploymentMode.SingleInstance, Budget, enableTargetPreparation: true),
            new(), ServiceId.Parse("wal-recovery"), Token);
        Assert.True(result.Succeeded);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Real_active_writer_is_bounded_and_fails_closed_without_application_writes()
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "busy.db");
        await CrashAsync(path);
        await using var writer = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ConnectionString);
        await writer.OpenAsync(Token);
        await using var write = writer.CreateCommand();
        write.CommandText = "BEGIN IMMEDIATE; INSERT INTO proof VALUES (99);";
        await write.ExecuteNonQueryAsync(Token);
        var access = new CountingAccess(new SqliteDatabaseAccess());
        var provider = new SqliteDatabaseTargetPreparationProvider(new SqliteTargetFileSystem(), access,
            recoveryOptions: new(true, TimeSpan.FromMilliseconds(300)));
        var stopwatch = Stopwatch.StartNew();
        var result = await provider.ObserveAsync(Target(path), Token);
        Assert.Contains(result.ErrorCode, new[] { WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, WellKnownDatabaseTargetPreparationErrorCodes.Timeout });
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(5));
        Assert.Equal(1, access.Recoveries);
        Assert.True(File.Exists(path + "-wal"));
        write.CommandText = "ROLLBACK; SELECT count(*) FROM proof";
        Assert.Equal(1L, await write.ExecuteScalarAsync(Token));
    }

    [Theory]
    [InlineData("journal")]
    [InlineData("directory")]
    [InlineData("dangling")]
    [InlineData("symbolic")]
    [InlineData("hardlink")]
    [InlineData("suffix-alias")]
    [InlineData("target-missing")]
    [InlineData("target-link")]
    [InlineData("ancestor-link")]
    [InlineData("permissions")]
    public async Task Unsafe_paths_never_enter_recovery_or_change_bytes(string scenario)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "unsafe.db");
        await File.WriteAllBytesAsync(path, [1, 2, 3], Token);
        var wal = path + "-wal";
        await File.WriteAllBytesAsync(wal, [4, 5, 6], Token);
        var original = path;
        UnixFileMode? mode = null;
        switch (scenario)
        {
            case "journal": await File.WriteAllBytesAsync(path + "-journal", [7], Token); break;
            case "directory": File.Delete(wal); Directory.CreateDirectory(wal); break;
            case "dangling": File.Delete(wal); File.CreateSymbolicLink(wal, path + "-absent"); break;
            case "symbolic": File.Delete(wal); File.CreateSymbolicLink(wal, path); break;
            case "hardlink": Assert.True(HardLink(path + "-alias", wal)); break;
            case "suffix-alias": File.Move(wal, path + "-WAL"); break;
            case "target-missing": File.Delete(path); break;
            case "target-link": path += "-link"; File.CreateSymbolicLink(path, original); await File.WriteAllBytesAsync(path + "-wal", [4], Token); break;
            case "ancestor-link":
                var linked = directory.Path + "-link";
                Directory.CreateSymbolicLink(linked, directory.Path);
                path = Path.Combine(linked, "unsafe.db");
                break;
            case "permissions":
                if (OperatingSystem.IsWindows()) { File.SetAttributes(wal, FileAttributes.ReadOnly); }
                else { mode = File.GetUnixFileMode(wal); File.SetUnixFileMode(wal, UnixFileMode.None); }
                break;
        }
        try
        {
            var entries = Directory.GetFileSystemEntries(directory.Path).Order().ToArray();
            var access = new CountingAccess(new SqliteDatabaseAccess());
            var provider = new SqliteDatabaseTargetPreparationProvider(new SqliteTargetFileSystem(), access, recoveryOptions: new(true));
            var observed = await provider.ObserveAsync(Target(path), Token);
            Assert.NotEqual(DatabaseTargetObservationStatus.TargetConnectable, observed.Status);
            Assert.Equal(0, access.Recoveries);
            Assert.Equal(entries, Directory.GetFileSystemEntries(directory.Path).Order().ToArray());
            if (scenario != "target-missing") Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(original, Token));
        }
        finally
        {
            if (scenario == "permissions")
            {
                if (OperatingSystem.IsWindows()) File.SetAttributes(wal, FileAttributes.Normal);
                else File.SetUnixFileMode(wal, mode!.Value);
            }
            if (scenario == "ancestor-link") Directory.Delete(directory.Path + "-link");
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Recovery_is_attempted_once_and_readonly_observation_once_without_recursion(bool sidecarsRemain)
    {
        var fs = new RecoveryFileSystem(sidecarsRemain);
        var access = new ScriptedAccess();
        var provider = new SqliteDatabaseTargetPreparationProvider(fs, access, recoveryOptions: new(true));
        var result = await provider.ObserveAsync(Target(AbsolutePath), Token);
        Assert.Equal(1, access.Recoveries);
        Assert.Equal(sidecarsRemain ? 0 : 1, access.Inspections);
        Assert.Equal(sidecarsRemain ? WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict : null, result.ErrorCode);
    }

    [Theory]
    [InlineData("preflight", false)]
    [InlineData("recovery", false)]
    [InlineData("recovery", true)]
    [InlineData("observe", false)]
    [InlineData("observe", true)]
    public async Task Caller_cancellation_at_completion_wins_over_results_and_internal_exceptions(string stage, bool throwFailure)
    {
        using var cts = new CancellationTokenSource();
        var fs = new RecoveryFileSystem(false) { Preflight = stage == "preflight" ? () => cts.Cancel() : null };
        var access = new ScriptedAccess { Recover = stage == "recovery" ? () => { cts.Cancel(); if (throwFailure) throw new InvalidOperationException("internal-private-material"); } : null,
            Inspect = stage == "observe" ? () => { cts.Cancel(); if (throwFailure) throw new InvalidOperationException("internal-private-material"); } : null };
        var provider = new SqliteDatabaseTargetPreparationProvider(fs, access, recoveryOptions: new(true));
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ObserveAsync(Target(AbsolutePath), cts.Token).AsTask());
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain("internal-private-material", exception.ToString());
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public async Task Real_owned_resources_are_released_even_when_cancellation_occurs_during_cleanup(int stage)
    {
        using var directory = new TestDirectory();
        var path = Path.Combine(directory.Path, "cancel.db");
        await CrashAsync(path);
        using var cts = new CancellationTokenSource();
        var released = false;
        var access = new SqliteDatabaseAccess((checkpoint, state, _) =>
        {
            if (checkpoint == SqliteRecoveryCheckpoint.ResourcesReleased) { Assert.Equal(System.Data.ConnectionState.Closed, state); released = true; }
            if ((int)checkpoint == stage) cts.Cancel();
            return ValueTask.CompletedTask;
        });
        var provider = new SqliteDatabaseTargetPreparationProvider(new SqliteTargetFileSystem(), access, recoveryOptions: new(true));
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => provider.ObserveAsync(Target(path), cts.Token).AsTask());
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.True(released);
        // FileShare.None detects a leaked owned handle on Windows; Unix relies on the lifecycle hook.
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Fact]
    public async Task Internal_timeout_and_internal_cancellation_are_finite_failures()
    {
        var fs = new RecoveryFileSystem(false);
        var access = new ScriptedAccess { Delay = true };
        var provider = new SqliteDatabaseTargetPreparationProvider(fs, access, recoveryOptions: new(true, TimeSpan.FromMilliseconds(20)));
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, (await provider.ObserveAsync(Target(AbsolutePath), Token)).ErrorCode);
        access = new() { Recover = () => throw new OperationCanceledException("private-internal") };
        provider = new(new RecoveryFileSystem(false), access, recoveryOptions: new(true));
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, (await provider.ObserveAsync(Target(AbsolutePath), Token)).ErrorCode);
    }

    [Fact]
    public async Task Recovery_options_preserve_defaults_validate_budget_and_precancel_before_IO()
    {
        Assert.False(new SqliteTargetRecoveryOptions().Enabled);
        Assert.Equal(TimeSpan.FromSeconds(30), new SqliteTargetRecoveryOptions().RecoveryTimeout);
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteTargetRecoveryOptions(true, TimeSpan.Zero));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteTargetRecoveryOptions(true, Timeout.InfiniteTimeSpan));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SqliteTargetRecoveryOptions(true, TimeSpan.MaxValue));
        Assert.Throws<ArgumentNullException>(() => new SqliteDatabaseTargetPreparationProvider(null!));
        var fs = new RecoveryFileSystem(false);
        var access = new ScriptedAccess();
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => new SqliteDatabaseTargetPreparationProvider(fs, access, recoveryOptions: new(true)).ObserveAsync(Target(AbsolutePath), cts.Token).AsTask());
        Assert.Equal(0, fs.PathInspections);
        Assert.Equal(0, access.Recoveries);
    }

    private static string AbsolutePath => OperatingSystem.IsWindows() ? @"C:\recovery\test.db" : "/recovery/test.db";
    private static BootstrapDatabaseConfiguration Target(string path) => new(WellKnownDatabaseProviderIds.Sqlite, null, new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);
    private static SqliteDatabaseTargetPreparationProvider Enabled() => new(new SqliteTargetRecoveryOptions(true, Budget));

    private static async Task CreateCleanWalAsync(string path)
    {
        await using (var connection = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Pooling = false }.ConnectionString))
        {
            await connection.OpenAsync(Token);
            await using var command = connection.CreateCommand();
            command.CommandText = "PRAGMA journal_mode=WAL; PRAGMA user_version=42; CREATE TABLE retained (id INTEGER); INSERT INTO retained VALUES (42);";
            await command.ExecuteNonQueryAsync(Token);
        }

        Assert.False(File.Exists(path + "-wal"));
        Assert.False(File.Exists(path + "-shm"));
        Assert.False(File.Exists(path + "-journal"));
    }

    private static async Task CrashAsync(string path)
    {
        var info = new ProcessStartInfo(Environment.ProcessPath!) { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false };
        if (Path.GetFileNameWithoutExtension(Environment.ProcessPath!).Equals("dotnet", StringComparison.OrdinalIgnoreCase)) info.ArgumentList.Add(Assembly.GetExecutingAssembly().Location);
        info.ArgumentList.Add("-explicit");
        info.ArgumentList.Add("on");
        info.ArgumentList.Add("-method");
        info.ArgumentList.Add("ServiceMantle.Database.Sqlite.Tests.SqliteWalRecoveryTests.Crash_writer_fixture");
        info.Environment["SERVICEMANTLE_SQLITE_CRASH_PATH"] = path;
        using var process = Process.Start(info)!;
        var output = process.StandardOutput.ReadToEndAsync(Token);
        var error = process.StandardError.ReadToEndAsync(Token);
        await process.WaitForExitAsync(Token).WaitAsync(TimeSpan.FromSeconds(20), Token);
        Assert.True(process.ExitCode == 0, await output + await error);
        Assert.True(File.Exists(path + "-wal"));
        Assert.True(File.Exists(path + "-shm"));
    }

    private sealed class TestDirectory : IDisposable
    {
        public string Path { get; }
        public TestDirectory()
        {
            var root = System.IO.Path.GetTempPath();
            if (OperatingSystem.IsMacOS() && root.StartsWith("/var/")) root = "/private" + root;
            Path = System.IO.Path.Combine(root, "ServiceMantle.Sqlite.Recovery", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }
        public void Dispose() => Directory.Delete(Path, true);
    }

    private sealed class Executor : IDatabaseMigrationExecutor
    {
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(MigrationObservationState.CurrentVersionCompatible);
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) => ValueTask.CompletedTask;
    }
    private sealed class CountingAccess(ISqliteDatabaseAccess inner) : ISqliteDatabaseAccess
    {
        public int Recoveries, ImmutableObservations;
        public ValueTask<SqliteDatabaseInspectionStatus> InspectAsync(string path, CancellationToken token) => inner.InspectAsync(path, token);
        public ValueTask<SqliteDatabaseInspectionStatus> InspectRecoveredAsync(string path, CancellationToken token) { ImmutableObservations++; return inner.InspectRecoveredAsync(path, token); }
        public ValueTask<bool> InspectCleanWalAsync(string path, CancellationToken token) => inner.InspectCleanWalAsync(path, token);
        public ValueTask InitializeAsync(string path, CancellationToken token) => inner.InitializeAsync(path, token);
        public ValueTask<SqliteRecoveryStatus> RecoverAsync(string path, TimeSpan timeout, CancellationToken token) { Recoveries++; return inner.RecoverAsync(path, timeout, token); }
    }
    private sealed class ScriptedAccess : ISqliteDatabaseAccess
    {
        public int Recoveries, Inspections, CleanWalChecks;
        public Action? Recover, Inspect, CleanWalCheck;
        public bool Delay, CleanWal;
        public SqliteRecoveryStatus RecoveryResult { get; set; } = SqliteRecoveryStatus.Recovered;
        public ValueTask<SqliteDatabaseInspectionStatus> InspectAsync(string path, CancellationToken token) { Inspections++; Inspect?.Invoke(); return ValueTask.FromResult(SqliteDatabaseInspectionStatus.Connectable); }
        public ValueTask<bool> InspectCleanWalAsync(string path, CancellationToken token) { CleanWalChecks++; CleanWalCheck?.Invoke(); return ValueTask.FromResult(CleanWal); }
        public ValueTask InitializeAsync(string path, CancellationToken token) => throw new InvalidOperationException();
        public async ValueTask<SqliteRecoveryStatus> RecoverAsync(string path, TimeSpan timeout, CancellationToken token)
        { Recoveries++; Recover?.Invoke(); if (Delay) await Task.Delay(Timeout.InfiniteTimeSpan, token); return RecoveryResult; }
    }
    private sealed class CleanWalFileSystem : ISqliteTargetFileSystem
    {
        public int PathInspections;
        public bool Eligible = true;
        public Action? Eligibility;
        public SqlitePathInspection Inspect(string path) { PathInspections++; return new(SqlitePathInspectionStatus.ExistingFile, path); }
        public SqliteSidecarInspectionStatus InspectSidecars(string path) => SqliteSidecarInspectionStatus.None;
        public bool CanRecoverCleanWal(string path) { Eligibility?.Invoke(); return Eligible; }
        public string CreateTemporaryFile(string path) => throw new InvalidOperationException();
        public SqlitePublishStatus Publish(string a, string b) => throw new InvalidOperationException();
        public void DeleteTemporaryFile(string path) => throw new InvalidOperationException();
    }
    private sealed class RecoveryFileSystem(bool remain) : ISqliteTargetFileSystem
    {
        public int PathInspections, SidecarInspections;
        public Action? Preflight;
        public SqlitePathInspection Inspect(string path) { PathInspections++; return new(SqlitePathInspectionStatus.ExistingFile, path); }
        public SqliteSidecarInspectionStatus InspectSidecars(string path) => ++SidecarInspections == 1 || remain ? SqliteSidecarInspectionStatus.Present : SqliteSidecarInspectionStatus.None;
        public bool CanRecoverSidecars(string path) { Preflight?.Invoke(); return true; }
        public string CreateTemporaryFile(string path) => throw new InvalidOperationException();
        public SqlitePublishStatus Publish(string a, string b) => throw new InvalidOperationException();
        public void DeleteTemporaryFile(string path) => throw new InvalidOperationException();
    }

    private static bool HardLink(string link, string target) => OperatingSystem.IsWindows() ? CreateHardLink(link, target, IntPtr.Zero) : Link(target, link) == 0;
    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)] private static extern bool CreateHardLink(string link, string target, IntPtr unused);
    [DllImport("libc", EntryPoint = "link", SetLastError = true)] private static extern int Link(string target, string link);
}
