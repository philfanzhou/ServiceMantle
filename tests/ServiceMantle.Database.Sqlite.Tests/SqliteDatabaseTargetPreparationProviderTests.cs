using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Sqlite;
using Xunit;

namespace ServiceMantle.Database.Sqlite.Tests;

public sealed class SqliteDatabaseTargetPreparationProviderTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public void Provider_declares_only_the_SQLite_file_and_single_instance_capabilities()
    {
        var provider = new SqliteDatabaseTargetPreparationProvider();

        Assert.Equal(WellKnownDatabaseProviderIds.Sqlite, provider.ProviderId);
        Assert.Equal(BootstrapDatabaseTargetKind.File, provider.TargetKind);
        Assert.Equal(WellKnownDatabaseProviderIds.Sqlite, provider.Capability.ProviderId);
        Assert.Equal(DatabaseDeploymentSupport.SingleInstanceOnly, provider.Capability.Support);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("current-segment")]
    [InlineData("parent-segment")]
    [InlineData("duplicate-separator")]
    [InlineData("trailing-separator")]
    [InlineData("data-directory")]
    [InlineData("empty-source")]
    [InlineData("memory-name")]
    [InlineData("memory-mode")]
    [InlineData("file-uri")]
    [InlineData("unc")]
    [InlineData("slash-unc")]
    [InlineData("device")]
    [InlineData("ads")]
    [InlineData("password")]
    [InlineData("vfs")]
    [InlineData("shared-cache")]
    [InlineData("read-only")]
    [InlineData("nul")]
    public async Task Invalid_input_matrix_fails_before_filesystem_or_SQLite_IO(string scenario)
    {
        using var directory = new TemporaryDirectory();
        var fileSystem = new RejectIoFileSystem();
        var database = new RejectIoDatabaseAccess();
        var provider = new SqliteDatabaseTargetPreparationProvider(fileSystem, database);
        var target = Target(InvalidConnectionString(scenario, directory.Path));

        var observation = await provider.ObserveAsync(target, Token);
        var preparation = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(target),
            DefaultTimeout,
            Token);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, preparation.ErrorCode);
        await AssertBootstrapResultAsync(provider, target, "database.connection_string_invalid");
        Assert.Equal(0, fileSystem.CallCount);
        Assert.Equal(0, database.CallCount);
    }

    [Theory]
    [InlineData("unspecified")]
    [InlineData("read-write-create")]
    [InlineData("read-write")]
    public async Task Supported_modes_enter_file_validation_without_creating_the_target(string mode)
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "supported.db");
        var connectionString = mode switch
        {
            "unspecified" => $"Data Source={path}",
            "read-write-create" => $"Data Source={path};Mode=ReadWriteCreate",
            "read-write" => $"Data Source={path};Mode=ReadWrite",
            _ => throw new ArgumentOutOfRangeException(nameof(mode))
        };

        var observation = await new SqliteDatabaseTargetPreparationProvider().ObserveAsync(
            Target(connectionString),
            Token);

        Assert.Equal(DatabaseTargetObservationStatus.TargetMissing, observation.Status);
        await AssertBootstrapResultAsync(new SqliteDatabaseTargetPreparationProvider(),
            Target(connectionString), "database.target_not_found");
        Assert.False(File.Exists(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Provider_mismatch_old_request_and_public_priority_rules_fail_before_file_IO()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "priority.db");
        var fileSystem = new RejectIoFileSystem();
        var provider = new SqliteDatabaseTargetPreparationProvider(
            fileSystem,
            new RejectIoDatabaseAccess());
        var target = TargetForPath(path);

        var oldRequest = new DatabaseTargetPreparationRequest(target, "Data Source=admin-secret");
        var oldResult = await provider.PrepareAsync(oldRequest, DefaultTimeout, Token);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, oldResult.ErrorCode);

        var mismatch = DatabaseTargetPreparationRequest.ForFile(new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            null,
            "not a SQLite connection string"));
        var mismatchResult = await provider.PrepareAsync(mismatch, DefaultTimeout, Token);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch, mismatchResult.ErrorCode);

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(target),
            TimeSpan.Zero,
            CancellationToken.None).AsTask());

        using var caller = new CancellationTokenSource();
        caller.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(target),
            TimeSpan.Zero,
            caller.Token).AsTask());
        Assert.Equal(0, fileSystem.CallCount);
    }

    [Fact]
    public async Task Missing_parent_and_missing_file_have_distinct_observations_without_writes()
    {
        using var directory = new TemporaryDirectory();
        var provider = new SqliteDatabaseTargetPreparationProvider();
        var missingParentPath = System.IO.Path.Combine(directory.Path, "absent", "target.db");
        var missingFilePath = System.IO.Path.Combine(directory.Path, "target.db");

        var missingParent = await provider.ObserveAsync(TargetForPath(missingParentPath), Token);
        var missingFile = await provider.ObserveAsync(TargetForPath(missingFilePath), Token);

        Assert.Equal(DatabaseTargetObservationStatus.ServerUnreachable, missingParent.Status);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, missingParent.ErrorCode);
        Assert.Equal(DatabaseTargetObservationStatus.TargetMissing, missingFile.Status);
        await AssertBootstrapResultAsync(provider, TargetForPath(missingParentPath),
            "database.connection_string_invalid");
        await AssertBootstrapResultAsync(provider, TargetForPath(missingFilePath), "database.target_not_found");
        Assert.False(Directory.Exists(System.IO.Path.GetDirectoryName(missingParentPath)));
        Assert.False(File.Exists(missingFilePath));
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Readable_database_is_observed_without_changing_bytes_or_directory_entries()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "existing.db");
        await CreateDatabaseAsync(path);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        var entries = Directory.GetFileSystemEntries(directory.Path);

        var observation = await new SqliteDatabaseTargetPreparationProvider().ObserveAsync(
            TargetForPath(path),
            Token);

        Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable, observation.Status);
        await AssertBootstrapResultAsync(new SqliteDatabaseTargetPreparationProvider(), TargetForPath(path), null);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
        Assert.Equal(entries, Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Existing_database_prepare_returns_already_exists_without_writes_or_sidecars()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "existing.db");
        await CreateDatabaseAsync(path);
        var bytes = await File.ReadAllBytesAsync(path, Token);
        var entries = Directory.GetFileSystemEntries(directory.Path);

        var result = await new SqliteDatabaseTargetPreparationProvider().PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(TargetForPath(path)),
            DefaultTimeout,
            Token);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, result.Outcome);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
        Assert.Equal(entries, Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Invalid_database_is_known_existing_but_connection_failed_without_side_effects()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "invalid.db");
        var bytes = new byte[] { 1, 2, 3, 4, 5 };
        await File.WriteAllBytesAsync(path, bytes, Token);

        var observation = await new SqliteDatabaseTargetPreparationProvider().ObserveAsync(
            TargetForPath(path),
            Token);

        Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, observation.Status);
        Assert.True(observation.TargetExists);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed, observation.ErrorCode);
        await AssertBootstrapResultAsync(new SqliteDatabaseTargetPreparationProvider(),
            TargetForPath(path), "database.connection_failed");
        Assert.Equal(bytes, await File.ReadAllBytesAsync(path, Token));
        Assert.Single(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Parent_observation_denial_and_unreadable_file_map_to_permission_denied()
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        using var directory = new TemporaryDirectory();
        var provider = new SqliteDatabaseTargetPreparationProvider();
        var deniedParent = System.IO.Path.Combine(directory.Path, "denied-parent");
        Directory.CreateDirectory(deniedParent);
        var missingPath = System.IO.Path.Combine(deniedParent, "missing.db");
        var unreadablePath = System.IO.Path.Combine(directory.Path, "unreadable.db");
        await CreateDatabaseAsync(unreadablePath);

        var originalDirectoryMode = File.GetUnixFileMode(deniedParent);
        var originalFileMode = File.GetUnixFileMode(unreadablePath);
        try
        {
            File.SetUnixFileMode(deniedParent, UnixFileMode.None);
            File.SetUnixFileMode(unreadablePath, UnixFileMode.None);

            var parentObservation = await provider.ObserveAsync(TargetForPath(missingPath), Token);
            var fileObservation = await provider.ObserveAsync(TargetForPath(unreadablePath), Token);

            Assert.Equal(DatabaseTargetObservationStatus.ServerUnreachable, parentObservation.Status);
            Assert.Equal(
                WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                parentObservation.ErrorCode);
            Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, fileObservation.Status);
            Assert.True(fileObservation.TargetExists);
            Assert.Equal(
                WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied,
                fileObservation.ErrorCode);
            await AssertBootstrapResultAsync(provider, TargetForPath(missingPath), "database.permission_denied");
            await AssertBootstrapResultAsync(provider, TargetForPath(unreadablePath), "database.permission_denied");
        }
        finally
        {
            File.SetUnixFileMode(deniedParent, originalDirectoryMode);
            File.SetUnixFileMode(unreadablePath, originalFileMode);
        }
    }

    [Theory]
    [InlineData((int)SqliteDatabaseInspectionStatus.PermissionDenied, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied)]
    [InlineData((int)SqliteDatabaseInspectionStatus.TargetConflict, WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict)]
    [InlineData((int)SqliteDatabaseInspectionStatus.ConnectionFailed, WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed)]
    public async Task Existing_target_failures_use_the_finite_observation_classification(
        int databaseStatusValue,
        string expectedErrorCode)
    {
        var databaseStatus = (SqliteDatabaseInspectionStatus)databaseStatusValue;
        var fileSystem = new StaticFileSystem(new SqlitePathInspection(
            SqlitePathInspectionStatus.ExistingFile,
            PlatformAbsolutePath("classified.db")));
        var database = new StaticDatabaseAccess(databaseStatus);
        var provider = new SqliteDatabaseTargetPreparationProvider(fileSystem, database);
        var target = TargetForPath(PlatformAbsolutePath("classified.db"));

        var observation = await provider.ObserveAsync(target, Token);
        var preparation = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(target),
            DefaultTimeout,
            Token);

        Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, observation.Status);
        Assert.True(observation.TargetExists);
        Assert.Equal(expectedErrorCode, observation.ErrorCode);
        Assert.Equal(expectedErrorCode, preparation.ErrorCode);
        Assert.Equal(2, database.InspectCalls);
        Assert.Equal(0, database.InitializeCalls);
    }

    [Fact]
    public async Task Unreliable_file_metadata_fails_closed_as_capability_not_supported()
    {
        var fileSystem = new StaticFileSystem(new SqlitePathInspection(
            SqlitePathInspectionStatus.CapabilityNotSupported));
        var database = new RejectIoDatabaseAccess();
        var provider = new SqliteDatabaseTargetPreparationProvider(fileSystem, database);
        var target = TargetForPath(PlatformAbsolutePath("unsupported.db"));

        var observation = await provider.ObserveAsync(target, Token);
        var preparation = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(target),
            DefaultTimeout,
            Token);
        var identityException = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            provider.GetCanonicalTargetIdentityAsync(target, Token).AsTask());

        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported,
            observation.ErrorCode);
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported,
            preparation.ErrorCode);
        Assert.DoesNotContain(target.ConnectionString, identityException.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, database.CallCount);
    }

    [Theory]
    [InlineData("-journal")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    public async Task SQLite_sidecars_fail_closed_as_a_known_target_conflict(string suffix)
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "sidecar.db");
        await CreateDatabaseAsync(path);
        await File.WriteAllBytesAsync(path + suffix, [1], Token);
        var entries = Directory.GetFileSystemEntries(directory.Path);

        var observation = await new SqliteDatabaseTargetPreparationProvider().ObserveAsync(
            TargetForPath(path),
            Token);

        Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, observation.Status);
        Assert.True(observation.TargetExists);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, observation.ErrorCode);
        Assert.Equal(entries, Directory.GetFileSystemEntries(directory.Path));
    }

    public static TheoryData<string, bool, string, string> SidecarPathCases => new(
        from suffix in new[] { "-journal", "-wal", "-shm" }
        from targetExists in new[] { false, true }
        from spelling in new[] { "exact", "leaf-case", "suffix-case" }
        from entryKind in new[] { "file", "directory", "dangling-link" }
        select (suffix, targetExists, spelling, entryKind));

    [Theory]
    [MemberData(nameof(SidecarPathCases))]
    public async Task Sidecar_conflicts_follow_filesystem_resolution_without_SQLite_IO_or_writes(
        string suffix,
        bool targetExists,
        string spelling,
        string entryKind)
    {
        using var directory = new TemporaryDirectory();
        // Detect this directory's behavior; Windows and macOS can also host case-sensitive volumes.
        var probe = System.IO.Path.Combine(directory.Path, "CaseProbe");
        await File.WriteAllTextAsync(probe, "probe", Token);
        var ignoresCase = File.Exists(System.IO.Path.Combine(directory.Path, "caseprobe"));
        File.Delete(probe);

        var path = System.IO.Path.Combine(directory.Path, "Target.db");
        if (targetExists)
        {
            await CreateDatabaseAsync(path);
        }

        var originalBytes = targetExists ? await File.ReadAllBytesAsync(path, Token) : null;
        var sidecarName = spelling switch
        {
            "leaf-case" => "target.db" + suffix,
            "suffix-case" => "Target.db" + suffix.ToUpperInvariant(),
            _ => "Target.db" + suffix
        };
        var sidecarPath = System.IO.Path.Combine(directory.Path, sidecarName);
        var linkTarget = System.IO.Path.Combine(directory.Path, "absent-link-target");
        switch (entryKind)
        {
            case "file":
                await File.WriteAllBytesAsync(sidecarPath, [1, 2, 3], Token);
                break;
            case "directory":
                Directory.CreateDirectory(sidecarPath);
                break;
            case "dangling-link":
                File.CreateSymbolicLink(sidecarPath, linkTarget);
                break;
        }

        var entries = Directory.GetFileSystemEntries(directory.Path)
            .Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray();
        var expectConflict = spelling == "exact" || ignoresCase;
        var rejectedDatabase = new RejectIoDatabaseAccess();
        var provider = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(),
            expectConflict ? rejectedDatabase : new SqliteDatabaseAccess());
        var target = TargetForPath(path);

        var observation = await provider.ObserveAsync(target, Token);
        await AssertBootstrapResultAsync(provider, target, expectConflict
            ? "database.connection_failed"
            : targetExists ? null : "database.target_not_found");
        Assert.Equal(entries, Directory.GetFileSystemEntries(directory.Path)
            .Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        var preparation = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(target), DefaultTimeout, Token);

        if (expectConflict)
        {
            Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, observation.Status);
            Assert.Equal(targetExists ? (bool?)true : null, observation.TargetExists);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, observation.ErrorCode);
            Assert.False(preparation.Succeeded);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, preparation.ErrorCode);
            Assert.Equal(0, rejectedDatabase.CallCount);
            Assert.Equal(targetExists, File.Exists(path));
        }
        else
        {
            Assert.Equal(targetExists
                ? DatabaseTargetObservationStatus.TargetConnectable
                : DatabaseTargetObservationStatus.TargetMissing, observation.Status);
            Assert.True(preparation.Succeeded);
            Assert.Equal(targetExists
                ? DatabaseTargetPreparationOutcome.AlreadyExists
                : DatabaseTargetPreparationOutcome.Created, preparation.Outcome);
            if (!targetExists)
            {
                entries = entries.Append("Target.db").Order(StringComparer.Ordinal).ToArray();
            }
        }

        Assert.Equal(entries, Directory.GetFileSystemEntries(directory.Path)
            .Select(System.IO.Path.GetFileName).Order(StringComparer.Ordinal).ToArray());
        if (targetExists)
        {
            Assert.Equal(originalBytes, await File.ReadAllBytesAsync(path, Token));
        }

        if (entryKind == "file")
        {
            Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(sidecarPath, Token));
        }
        else if (entryKind == "directory")
        {
            Assert.True(Directory.Exists(sidecarPath));
            Assert.Empty(Directory.EnumerateFileSystemEntries(sidecarPath));
        }
        else
        {
            Assert.True(new FileInfo(sidecarPath).LinkTarget == linkTarget);
            Assert.False(File.Exists(linkTarget));
        }
    }

    [Fact]
    public async Task Directory_symbolic_linked_component_hard_link_and_special_file_are_rejected()
    {
        using var directory = new TemporaryDirectory();
        var provider = new SqliteDatabaseTargetPreparationProvider();

        var directoryTarget = System.IO.Path.Combine(directory.Path, "directory.db");
        Directory.CreateDirectory(directoryTarget);
        await AssertInvalidTargetAsync(provider, directoryTarget);

        var original = System.IO.Path.Combine(directory.Path, "original.db");
        await CreateDatabaseAsync(original);
        var symbolic = System.IO.Path.Combine(directory.Path, "symbolic.db");
        File.CreateSymbolicLink(symbolic, original);
        await AssertInvalidTargetAsync(provider, symbolic);

        var linkedParent = System.IO.Path.Combine(directory.Path, "linked-parent");
        Directory.CreateSymbolicLink(linkedParent, directory.Path);
        await AssertInvalidTargetAsync(provider, System.IO.Path.Combine(linkedParent, "missing.db"));

        var hardLink = System.IO.Path.Combine(directory.Path, "hardlink.db");
        Assert.True(CreateHardLink(hardLink, original));
        await AssertInvalidTargetAsync(provider, original);
        await AssertInvalidTargetAsync(provider, hardLink);

        if (!OperatingSystem.IsWindows())
        {
            var fifo = System.IO.Path.Combine(directory.Path, "special.db");
            Assert.Equal(0, MakeFifo(fifo, Convert.ToUInt32("600", 8)));
            await AssertInvalidTargetAsync(provider, fifo);
        }
    }

    [Fact]
    public async Task Missing_target_is_initialized_and_published_without_temporary_files()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "created.db");
        var provider = new SqliteDatabaseTargetPreparationProvider();

        var result = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(TargetForPath(path)),
            DefaultTimeout,
            Token);
        var observation = await provider.ObserveAsync(TargetForPath(path), Token);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.Created, result.Outcome);
        Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable, observation.Status);
        Assert.True(new FileInfo(path).Length > 0);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Concurrent_publish_has_one_creator_and_never_overwrites_the_winner()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "race.db");
        var arrived = 0;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async ValueTask Checkpoint(SqlitePreparationCheckpoint checkpoint, CancellationToken token)
        {
            if (checkpoint != SqlitePreparationCheckpoint.BeforePublish)
            {
                return;
            }

            if (Interlocked.Increment(ref arrived) == 2)
            {
                release.TrySetResult();
            }

            await release.Task.WaitAsync(token);
        }

        var first = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(), new SqliteDatabaseAccess(), Checkpoint);
        var second = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(), new SqliteDatabaseAccess(), Checkpoint);
        var request = DatabaseTargetPreparationRequest.ForFile(TargetForPath(path));

        var results = await Task.WhenAll(
            first.PrepareAsync(request, DefaultTimeout, Token).AsTask(),
            second.PrepareAsync(request, DefaultTimeout, Token).AsTask());

        Assert.Contains(results, result => result.Outcome == DatabaseTargetPreparationOutcome.Created);
        Assert.Contains(results, result => result.Outcome == DatabaseTargetPreparationOutcome.AlreadyExists);
        Assert.All(results, result => Assert.True(result.Succeeded));
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
        Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable,
            (await first.ObserveAsync(TargetForPath(path), Token)).Status);
    }

    [Fact]
    public async Task Publish_race_preserves_and_reobserves_a_distinct_winner_database()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "winner.db");
        var provider = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(),
            new SqliteDatabaseAccess(),
            async (checkpoint, _) =>
            {
                if (checkpoint == SqlitePreparationCheckpoint.BeforePublish)
                {
                    await CreateDatabaseAsync(path, userVersion: 42);
                }
            });

        var result = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(TargetForPath(path)),
            DefaultTimeout,
            Token);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, result.Outcome);
        Assert.Equal(42L, await ReadUserVersionAsync(path));
        var entries = Directory.GetFileSystemEntries(directory.Path);
        Assert.Single(entries);
        Assert.Equal("winner.db", System.IO.Path.GetFileName(entries[0]));
    }

    [Fact]
    public async Task Timeout_before_publish_cleans_only_this_calls_temporary_files()
    {
        using var directory = new TemporaryDirectory();
        var preserved = System.IO.Path.Combine(directory.Path, "preserved.txt");
        await File.WriteAllTextAsync(preserved, "keep", Token);
        var path = System.IO.Path.Combine(directory.Path, "timeout.db");
        var provider = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(),
            new SqliteDatabaseAccess(),
            async (checkpoint, token) =>
            {
                if (checkpoint == SqlitePreparationCheckpoint.TemporaryFileCreated)
                {
                    await Task.Delay(Timeout.InfiniteTimeSpan, token);
                }
            });

        var result = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(TargetForPath(path)),
            TimeSpan.FromMilliseconds(50),
            Token);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, result.ErrorCode);
        Assert.False(File.Exists(path));
        Assert.Equal([preserved], Directory.GetFileSystemEntries(directory.Path));
        Assert.Equal("keep", await File.ReadAllTextAsync(preserved, Token));
    }

    [Fact]
    public async Task Caller_cancellation_before_publish_is_sanitized_and_cleans_the_temporary_file()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "cancel.db");
        using var caller = new CancellationTokenSource();
        var provider = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(),
            new SqliteDatabaseAccess(),
            (checkpoint, token) =>
            {
                if (checkpoint == SqlitePreparationCheckpoint.TemporaryFileCreated)
                {
                    caller.Cancel();
                    throw new InvalidOperationException("Password=internal-cancel-secret");
                }

                return ValueTask.CompletedTask;
            });

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(TargetForPath(path)),
            DefaultTimeout,
            caller.Token).AsTask());

        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(path, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("internal-cancel-secret", exception.ToString(), StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Cancellation_after_publish_leaves_a_complete_target_for_an_already_exists_retry()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "committed.db");
        using var caller = new CancellationTokenSource();
        var provider = new SqliteDatabaseTargetPreparationProvider(
            new SqliteTargetFileSystem(),
            new SqliteDatabaseAccess(),
            (checkpoint, _) =>
            {
                if (checkpoint == SqlitePreparationCheckpoint.AfterPublish)
                {
                    caller.Cancel();
                }

                return ValueTask.CompletedTask;
            });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(TargetForPath(path)),
            DefaultTimeout,
            caller.Token).AsTask());

        var retry = await new SqliteDatabaseTargetPreparationProvider().PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(TargetForPath(path)),
            DefaultTimeout,
            Token);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, retry.Outcome);
        Assert.Equal([path], Directory.GetFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Canonical_identity_is_stable_private_path_only_and_does_not_create_a_file()
    {
        using var directory = new TemporaryDirectory();
        var firstPath = System.IO.Path.Combine(directory.Path, "first.db");
        var secondPath = System.IO.Path.Combine(directory.Path, "second.db");
        var provider = new SqliteDatabaseTargetPreparationProvider();
        var first = Target($"Data Source={firstPath};Mode=ReadWriteCreate;Password=");
        var equivalent = Target($"Filename={firstPath};Cache=Private;Pooling=False");
        var different = TargetForPath(secondPath);

        var firstIdentity = await provider.GetCanonicalTargetIdentityAsync(first, Token);
        var equivalentIdentity = await provider.GetCanonicalTargetIdentityAsync(equivalent, Token);
        var differentIdentity = await provider.GetCanonicalTargetIdentityAsync(different, Token);

        Assert.Equal(firstIdentity, equivalentIdentity);
        Assert.NotEqual(firstIdentity, differentIdentity);
        Assert.StartsWith("sqlite-file-sha256:", firstIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(firstPath, firstIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain(first.ConnectionString, firstIdentity, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", firstIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task Existing_components_use_filesystem_spelling_when_the_platform_resolves_an_alias()
    {
        using var directory = new TemporaryDirectory();
        var actualParent = System.IO.Path.Combine(directory.Path, "ExactParent");
        Directory.CreateDirectory(actualParent);
        var submittedParent = System.IO.Path.Combine(directory.Path, "exactparent");
        if (!Directory.Exists(submittedParent))
        {
            return;
        }

        var actualPath = System.IO.Path.Combine(actualParent, "NewLeaf.db");
        var submittedPath = System.IO.Path.Combine(submittedParent, "NewLeaf.db");
        var provider = new SqliteDatabaseTargetPreparationProvider();

        var actualIdentity = await provider.GetCanonicalTargetIdentityAsync(
            TargetForPath(actualPath), Token);
        var submittedIdentity = await provider.GetCanonicalTargetIdentityAsync(
            TargetForPath(submittedPath), Token);

        Assert.Equal(actualIdentity, submittedIdentity);
        Assert.False(File.Exists(actualPath));
    }

    [Theory]
    [InlineData(DatabaseDeploymentMode.Unspecified, false)]
    [InlineData(DatabaseDeploymentMode.SingleInstance, true)]
    [InlineData(DatabaseDeploymentMode.MultiInstance, false)]
    [InlineData((DatabaseDeploymentMode)999, false)]
    public void Core_validator_enforces_the_SQLite_single_only_mode_matrix(
        DatabaseDeploymentMode mode,
        bool expected)
    {
        var provider = new SqliteDatabaseTargetPreparationProvider();
        var registry = new DatabaseDeploymentCapabilityRegistry(
            [provider],
            DatabaseProviderIdResolver.Empty);

        var result = new DatabaseDeploymentValidator(registry).Validate(
            WellKnownDatabaseProviderIds.Sqlite,
            mode);

        Assert.Equal(expected, result.IsSupported);
        Assert.Equal(
            expected ? null : WellKnownDatabaseTargetPreparationErrorCodes.CapabilityNotSupported,
            result.PreparationErrorCode);
    }

    [Fact]
    public void Explicit_AspNetCore_registration_is_idempotent_resolvable_and_still_opt_in()
    {
        var services = new ServiceCollection();
        var builder = services.AddServiceMantle(
            ServiceId.Parse("sqlite-registration"),
            InstanceId.Parse("sqlite-registration-01"));
        builder.AddDatabaseTargetPreparationProvider<SqliteDatabaseTargetPreparationProvider>();
        builder.AddDatabaseTargetPreparationProvider<SqliteDatabaseTargetPreparationProvider>();

        using var provider = services.BuildServiceProvider();
        Assert.Single(provider.GetServices<IDatabaseTargetPreparationProvider>());
        var registry = provider.GetRequiredService<DatabaseTargetPreparationProviderRegistry>();
        Assert.True(registry.TryGetProvider(WellKnownDatabaseProviderIds.Sqlite, out var resolved));
        Assert.IsType<SqliteDatabaseTargetPreparationProvider>(resolved);

        var unregisteredServices = new ServiceCollection();
        unregisteredServices.AddServiceMantle(
            ServiceId.Parse("sqlite-unregistered"),
            InstanceId.Parse("sqlite-unregistered-01"));
        using var unregistered = unregisteredServices.BuildServiceProvider();
        Assert.False(unregistered.GetRequiredService<DatabaseTargetPreparationProviderRegistry>()
            .TryGetProvider(WellKnownDatabaseProviderIds.Sqlite, out _));
    }

    [Fact]
    public async Task Results_exceptions_and_default_serialization_never_include_connection_or_path_material()
    {
        using var directory = new TemporaryDirectory();
        var path = System.IO.Path.Combine(directory.Path, "private-name.db");
        const string secret = "sqlite-password-secret";
        var provider = new SqliteDatabaseTargetPreparationProvider();
        var target = Target($"Data Source={path};Password={secret}");

        var observation = await provider.ObserveAsync(target, Token);
        var result = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(target),
            DefaultTimeout,
            Token);
        var output = observation + result.ToString() +
            JsonSerializer.Serialize(observation) + JsonSerializer.Serialize(result);

        Assert.DoesNotContain(path, output, StringComparison.Ordinal);
        Assert.DoesNotContain(target.ConnectionString, output, StringComparison.Ordinal);
        Assert.DoesNotContain(secret, output, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    private static async Task AssertInvalidTargetAsync(
        SqliteDatabaseTargetPreparationProvider provider,
        string path)
    {
        var observation = await provider.ObserveAsync(TargetForPath(path), Token);
        var preparation = await provider.PrepareAsync(
            DatabaseTargetPreparationRequest.ForFile(TargetForPath(path)),
            DefaultTimeout,
            Token);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, preparation.ErrorCode);
        await AssertBootstrapResultAsync(provider, TargetForPath(path), "database.connection_string_invalid");
    }

    private static async Task AssertBootstrapResultAsync(
        SqliteDatabaseTargetPreparationProvider observer,
        BootstrapDatabaseConfiguration target,
        string? expectedCode)
    {
        var result = await new SqliteBootstrapDatabaseProvider(observer).ValidateAsync(target, Token);
        Assert.Equal(expectedCode is null, result.IsValid);
        Assert.Equal(expectedCode, result.ErrorCode);
        var output = result + JsonSerializer.Serialize(result);
        Assert.DoesNotContain(target.ConnectionString, output, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", output, StringComparison.OrdinalIgnoreCase);
        if (SqliteFileTarget.TryParse(target.ConnectionString, out var path))
        {
            Assert.DoesNotContain(path, output, StringComparison.Ordinal);
        }
    }

    private static string InvalidConnectionString(string scenario, string directory)
    {
        var target = System.IO.Path.Combine(directory, "target.db");
        var root = System.IO.Path.GetPathRoot(target)!;
        return scenario switch
        {
            "relative" => "Data Source=target.db",
            "current-segment" => $"Data Source={System.IO.Path.Combine(directory, ".", "target.db")}",
            "parent-segment" => $"Data Source={directory}{System.IO.Path.DirectorySeparatorChar}child{System.IO.Path.DirectorySeparatorChar}..{System.IO.Path.DirectorySeparatorChar}target.db",
            "duplicate-separator" => $"Data Source={directory}{System.IO.Path.DirectorySeparatorChar}{System.IO.Path.DirectorySeparatorChar}target.db",
            "trailing-separator" => $"Data Source={target}{System.IO.Path.DirectorySeparatorChar}",
            "data-directory" => "Data Source=|DataDirectory|target.db",
            "empty-source" => "Data Source=",
            "memory-name" => "Data Source=:memory:",
            "memory-mode" => $"Data Source={target};Mode=Memory",
            "file-uri" => "Data Source=file:/tmp/target.db",
            "unc" => "Data Source=\\\\server\\share\\target.db",
            "slash-unc" => "Data Source=//server/share/target.db",
            "device" => "Data Source=\\\\?\\C:\\target.db",
            "ads" => OperatingSystem.IsWindows()
                ? $"Data Source={root}target.db:stream"
                : "Data Source=C:\\target.db:stream",
            "password" => $"Data Source={target};Password=secret",
            "vfs" => $"Data Source={target};Vfs=custom",
            "shared-cache" => $"Data Source={target};Cache=Shared",
            "read-only" => $"Data Source={target};Mode=ReadOnly",
            "nul" => "Data Source=bad\0name",
            _ => throw new ArgumentOutOfRangeException(nameof(scenario))
        };
    }

    private static BootstrapDatabaseConfiguration TargetForPath(string path) =>
        Target(new SqliteConnectionStringBuilder { DataSource = path }.ConnectionString);

    private static string PlatformAbsolutePath(string leaf) => OperatingSystem.IsWindows()
        ? $"C:\\servicemantle-tests\\{leaf}"
        : $"/servicemantle-tests/{leaf}";

    private static BootstrapDatabaseConfiguration Target(string connectionString) =>
        new(WellKnownDatabaseProviderIds.Sqlite, null, connectionString);

    private static async Task CreateDatabaseAsync(string path, int userVersion = 1)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA user_version = {userVersion}";
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<long> ReadUserVersionAsync(string path)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false
        }.ConnectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version";
        return (long)(await command.ExecuteScalarAsync(Token))!;
    }

    private static bool CreateHardLink(string linkPath, string existingPath)
    {
        if (OperatingSystem.IsWindows())
        {
            return CreateHardLinkWindows(linkPath, existingPath, IntPtr.Zero);
        }

        return CreateHardLinkUnix(existingPath, linkPath) == 0;
    }

    [DllImport("libc", EntryPoint = "link", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int CreateHardLinkUnix(string existingPath, string linkPath);

    [DllImport("libc", EntryPoint = "mkfifo", SetLastError = true, CharSet = CharSet.Ansi)]
    private static extern int MakeFifo(string path, uint mode);

    [DllImport("kernel32.dll", EntryPoint = "CreateHardLinkW", SetLastError = true, CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkWindows(
        string fileName,
        string existingFileName,
        IntPtr securityAttributes);

    private sealed class TemporaryDirectory : IDisposable
    {
        internal TemporaryDirectory()
        {
            var temporaryRoot = System.IO.Path.GetTempPath();
            if (OperatingSystem.IsMacOS() && temporaryRoot.StartsWith("/var/", StringComparison.Ordinal))
            {
                temporaryRoot = "/private" + temporaryRoot;
            }

            Path = System.IO.Path.Combine(
                temporaryRoot,
                "ServiceMantle.Sqlite.Tests",
                Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(Path);
        }

        internal string Path { get; }

        public void Dispose()
        {
            try
            {
                Directory.Delete(Path, recursive: true);
            }
            catch
            {
                // Test cleanup must not hide the assertion that originally failed.
            }
        }
    }

    private sealed class RejectIoFileSystem : ISqliteTargetFileSystem
    {
        internal int CallCount;
        public SqlitePathInspection Inspect(string path)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("Filesystem I/O must not be reached.");
        }

        public SqliteSidecarInspectionStatus InspectSidecars(string canonicalPath) =>
            throw new InvalidOperationException("Filesystem I/O must not be reached.");
        public string CreateTemporaryFile(string canonicalTargetPath) =>
            throw new InvalidOperationException("Filesystem I/O must not be reached.");
        public SqlitePublishStatus Publish(string temporaryPath, string canonicalTargetPath) =>
            throw new InvalidOperationException("Filesystem I/O must not be reached.");
        public void DeleteTemporaryFile(string temporaryPath) =>
            throw new InvalidOperationException("Filesystem I/O must not be reached.");
    }

    private sealed class RejectIoDatabaseAccess : ISqliteDatabaseAccess
    {
        internal int CallCount;
        public ValueTask<SqliteDatabaseInspectionStatus> InspectAsync(
            string canonicalPath,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("SQLite I/O must not be reached.");
        }

        public ValueTask InitializeAsync(string temporaryPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref CallCount);
            throw new InvalidOperationException("SQLite I/O must not be reached.");
        }
    }

    private sealed class StaticFileSystem(SqlitePathInspection inspection) : ISqliteTargetFileSystem
    {
        public SqlitePathInspection Inspect(string path) => inspection;
        public SqliteSidecarInspectionStatus InspectSidecars(string canonicalPath) =>
            SqliteSidecarInspectionStatus.None;
        public string CreateTemporaryFile(string canonicalTargetPath) =>
            throw new InvalidOperationException("Temporary creation must not be reached.");
        public SqlitePublishStatus Publish(string temporaryPath, string canonicalTargetPath) =>
            throw new InvalidOperationException("Publish must not be reached.");
        public void DeleteTemporaryFile(string temporaryPath) =>
            throw new InvalidOperationException("Cleanup must not be reached.");
    }

    private sealed class StaticDatabaseAccess(SqliteDatabaseInspectionStatus status) : ISqliteDatabaseAccess
    {
        internal int InspectCalls;
        internal int InitializeCalls;

        public ValueTask<SqliteDatabaseInspectionStatus> InspectAsync(
            string canonicalPath,
            CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InspectCalls);
            return ValueTask.FromResult(status);
        }

        public ValueTask InitializeAsync(string temporaryPath, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref InitializeCalls);
            return ValueTask.CompletedTask;
        }
    }
}
