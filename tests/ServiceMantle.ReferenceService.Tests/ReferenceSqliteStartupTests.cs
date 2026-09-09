using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.Sqlite;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Covers the sample's opt-in SQLite startup deployment gate: it is off unless it is switched on,
/// it refuses an unauthorized or unusable input before any side effect, and only a successful
/// migration lets the host finish starting.
/// </summary>
public sealed class ReferenceSqliteStartupTests
{
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task The_gate_is_off_by_default_and_the_skeleton_startup_is_unchanged()
    {
        using var directory = TemporaryDirectory.Create();

        var builder = ReferenceApplication.CreateBuilder(
            ["--ReferenceService:DatabasePath", directory.DatabasePath]);
        builder.WebHost.UseTestServer();
        await using var app = ReferenceApplication.Build(builder);
        await app.StartAsync(Token);

        using var client = app.GetTestClient();
        using var root = await client.GetAsync("/", Token);

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Contains("skeleton", await root.Content.ReadAsStringAsync(Token), StringComparison.Ordinal);
        Assert.Null(app.Services.GetService<ReferenceSqliteStartupCoordinator>());
        Assert.Null(app.Services.GetService<ReferenceSqliteStartupOptions>());
        await app.StopAsync(Token);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task The_switched_on_gate_runs_on_the_real_startup_path_and_migrates_the_workspace()
    {
        using var directory = TemporaryDirectory.Create();

        await using var app = await StartAsync(directory, prepareIfMissing: true);
        var step = app.Services.GetRequiredService<ReferenceSqliteStartupHostedService>();

        using var client = app.GetTestClient();
        using var root = await client.GetAsync("/", Token);

        Assert.Equal(HttpStatusCode.OK, root.StatusCode);
        Assert.Equal(ReferenceSqliteStartupOutcome.Ready, step.Result!.Outcome);
        Assert.True(step.Result.ExecutorWasCalled);
        Assert.True(File.Exists(directory.DatabasePath));
        Assert.Contains("reference_workspaces", await ReadTableNamesAsync(directory));
        await app.StopAsync(Token);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("Unspecified")]
    [InlineData("MultiInstance")]
    [InlineData("3")]
    [InlineData("not-a-mode")]
    public void An_unauthorized_deployment_mode_is_refused_before_any_provider_or_ef_call(string? mode)
    {
        using var directory = TemporaryDirectory.Create();
        var arguments = new List<string>
        {
            "--ReferenceService:DatabasePath", directory.DatabasePath,
            "--" + ReferenceSqliteStartupOptions.EnabledKey, "true",
        };
        if (mode is not null)
        {
            arguments.AddRange(["--" + ReferenceSqliteStartupOptions.DeploymentModeKey, mode]);
        }

        var failure = Assert.Throws<InvalidOperationException>(
            () => ReferenceApplication.CreateBuilder([.. arguments]));

        Assert.Contains(ReferenceSqliteStartupOptions.DeploymentModeKey, failure.Message, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(mode))
        {
            Assert.DoesNotContain(mode, failure.Message, StringComparison.Ordinal);
        }

        // Nothing was observed, prepared, or migrated, and no lock of any kind was constructed.
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task An_unregistered_deployment_capability_stops_the_gate_before_the_provider()
    {
        using var directory = TemporaryDirectory.Create();
        var options = ReadOptions(directory, prepareIfMissing: true);
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(ServiceId.Parse("reference-service"));
        services.AddSingleton(options);
        services.AddSingleton<IBootstrapDatabaseProvider, ServiceMantle.Database.Sqlite.SqliteBootstrapDatabaseProvider>();
        services.AddSingleton(provider => new BootstrapDatabaseProviderRegistry(
            provider.GetServices<IBootstrapDatabaseProvider>()));
        services.AddSingleton(provider => new DatabaseTargetPreparationProviderRegistry(
            [new ServiceMantle.Database.Sqlite.SqliteDatabaseTargetPreparationProvider()],
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        // A preparation provider is registered, but no deployment capability is: registering one
        // never implies the other.
        services.AddSingleton(provider => new DatabaseDeploymentCapabilityRegistry(
            providers: null,
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.AddSingleton(provider => new DatabaseMigrationLockProviderRegistry(
            providers: null,
            provider.GetRequiredService<BootstrapDatabaseProviderRegistry>().ProviderIdResolver));
        services.AddSingleton<ReferenceSqliteStartupCoordinator>();
        await using var container = services.BuildServiceProvider();

        var result = await container.GetRequiredService<ReferenceSqliteStartupCoordinator>()
            .RunAsync(Token);

        Assert.Equal(ReferenceSqliteStartupOutcome.DeploymentModeRejected, result.Outcome);
        Assert.False(result.ExecutorWasCalled);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("relative/reference.db")]
    public void An_unusable_database_path_is_refused_before_any_side_effect(string? path)
    {
        using var directory = TemporaryDirectory.Create();
        var arguments = new List<string>
        {
            "--" + ReferenceSqliteStartupOptions.EnabledKey, "true",
            "--" + ReferenceSqliteStartupOptions.DeploymentModeKey, "SingleInstance",
        };
        if (path is not null)
        {
            arguments.AddRange(["--ReferenceService:DatabasePath", path]);
        }

        var failure = Assert.Throws<InvalidOperationException>(
            () => ReferenceApplication.CreateBuilder([.. arguments]));

        Assert.Contains(ReferenceSqliteStartupOptions.DatabasePathKey, failure.Message, StringComparison.Ordinal);
        if (!string.IsNullOrEmpty(path))
        {
            Assert.DoesNotContain(path, failure.Message, StringComparison.Ordinal);
        }

        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public void An_unusable_preparation_switch_is_refused_before_any_side_effect()
    {
        using var directory = TemporaryDirectory.Create();

        var failure = Assert.Throws<InvalidOperationException>(() => ReferenceApplication.CreateBuilder(
        [
            "--ReferenceService:DatabasePath", directory.DatabasePath,
            "--" + ReferenceSqliteStartupOptions.EnabledKey, "true",
            "--" + ReferenceSqliteStartupOptions.DeploymentModeKey, "SingleInstance",
            "--" + ReferenceSqliteStartupOptions.PrepareIfMissingKey, "perhaps",
        ]));

        Assert.Contains(ReferenceSqliteStartupOptions.PrepareIfMissingKey, failure.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("perhaps", failure.Message, StringComparison.Ordinal);
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task A_missing_target_stops_the_startup_unless_preparation_was_permitted()
    {
        using var directory = TemporaryDirectory.Create();

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(directory, prepareIfMissing: false));

        Assert.Contains(
            ReferenceSqliteStartupOutcome.TargetMissing.ToString(),
            failure.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(directory.DatabasePath, failure.Message, StringComparison.Ordinal);
        // Nothing was created: neither a directory nor an empty file.
        Assert.Empty(Directory.EnumerateFileSystemEntries(directory.Path));
    }

    [Fact]
    public async Task A_restart_over_the_current_database_never_executes_the_migration_again()
    {
        using var directory = TemporaryDirectory.Create();

        await using (var first = await StartAsync(directory, prepareIfMissing: true))
        {
            Assert.True(first.Services
                .GetRequiredService<ReferenceSqliteStartupHostedService>().Result!.ExecutorWasCalled);
            await first.StopAsync(Token);
        }

        var bytes = await File.ReadAllBytesAsync(directory.DatabasePath, Token);

        await using var second = await StartAsync(directory, prepareIfMissing: false);
        var result = second.Services.GetRequiredService<ReferenceSqliteStartupHostedService>().Result!;

        Assert.Equal(ReferenceSqliteStartupOutcome.Ready, result.Outcome);
        Assert.False(result.ExecutorWasCalled);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(directory.DatabasePath, Token));
        await second.StopAsync(Token);
    }

    [Fact]
    public async Task An_unusable_existing_target_is_never_adopted_or_repaired()
    {
        using var directory = TemporaryDirectory.Create();
        await File.WriteAllTextAsync(directory.DatabasePath, "not a database at all", Token);
        var bytes = await File.ReadAllBytesAsync(directory.DatabasePath, Token);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(directory, prepareIfMissing: true));

        Assert.DoesNotContain(directory.DatabasePath, failure.Message, StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(directory.DatabasePath, Token));
    }

    [Fact]
    public async Task A_database_of_unknown_origin_stops_the_startup_without_being_touched()
    {
        using var directory = TemporaryDirectory.Create();
        await CreateDatabaseAsync(directory, """CREATE TABLE "someone_elses_table" ("Id" INTEGER)""");
        var bytes = await File.ReadAllBytesAsync(directory.DatabasePath, Token);

        var failure = await Assert.ThrowsAsync<InvalidOperationException>(
            () => StartAsync(directory, prepareIfMissing: false));

        Assert.Contains(
            ReferenceSqliteStartupOutcome.MigrationFailed.ToString(),
            failure.Message,
            StringComparison.Ordinal);
        Assert.Equal(bytes, await File.ReadAllBytesAsync(directory.DatabasePath, Token));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task The_startup_output_never_carries_a_path_a_connection_string_or_a_secret(
        bool loggingEnabled)
    {
        using var directory = TemporaryDirectory.Create();
        var recorder = new RecordingLoggerProvider();
        var options = ReadOptions(directory, prepareIfMissing: true);

        await using var app = await StartAsync(
            directory,
            prepareIfMissing: true,
            configure: builder =>
            {
                builder.Logging.ClearProviders();
                if (loggingEnabled)
                {
                    builder.Logging.AddProvider(recorder);
                    builder.Logging.SetMinimumLevel(LogLevel.Information);
                }
            });

        var result = app.Services.GetRequiredService<ReferenceSqliteStartupHostedService>().Result!;

        Assert.Equal(ReferenceSqliteStartupOutcome.Ready, result.Outcome);
        Assert.Equal(loggingEnabled, recorder.Lines.Count > 0);
        foreach (var line in recorder.Lines)
        {
            Assert.DoesNotContain(directory.DatabasePath, line, StringComparison.Ordinal);
            Assert.DoesNotContain(options.TargetConnectionString, line, StringComparison.Ordinal);
            Assert.DoesNotContain("Data Source", line, StringComparison.OrdinalIgnoreCase);
        }

        Assert.DoesNotContain(directory.DatabasePath, result.ToString(), StringComparison.Ordinal);
        await app.StopAsync(Token);
    }

    internal static ReferenceSqliteStartupOptions ReadOptions(
        TemporaryDirectory directory,
        bool prepareIfMissing) =>
        ReferenceSqliteStartupOptions.Read(new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                [ReferenceSqliteStartupOptions.EnabledKey] = "true",
                [ReferenceSqliteStartupOptions.DeploymentModeKey] = "SingleInstance",
                [ReferenceSqliteStartupOptions.PrepareIfMissingKey] =
                    prepareIfMissing ? "true" : "false",
                [ReferenceSqliteStartupOptions.DatabasePathKey] = directory.DatabasePath,
            })
            .Build())!;

    internal static async Task CreateDatabaseAsync(TemporaryDirectory directory, params string[] statements)
    {
        await using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = directory.DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Private,
            Pooling = false,
        }.ConnectionString);
        await connection.OpenAsync(Token);
        foreach (var statement in statements)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = statement;
            await command.ExecuteNonQueryAsync(Token);
        }
    }

    private static async Task<List<string>> ReadTableNamesAsync(TemporaryDirectory directory)
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
        command.CommandText = "SELECT name FROM sqlite_schema WHERE type = 'table'";
        var names = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            names.Add(reader.GetString(0));
        }

        return names;
    }

    private static async Task<WebApplication> StartAsync(
        TemporaryDirectory directory,
        bool prepareIfMissing,
        Action<WebApplicationBuilder>? configure = null)
    {
        var builder = ReferenceApplication.CreateBuilder(
        [
            "--ReferenceService:DatabasePath", directory.DatabasePath,
            "--" + ReferenceSqliteStartupOptions.EnabledKey, "true",
            "--" + ReferenceSqliteStartupOptions.DeploymentModeKey, "SingleInstance",
            "--" + ReferenceSqliteStartupOptions.PrepareIfMissingKey,
            prepareIfMissing ? "true" : "false",
        ]);
        builder.WebHost.UseTestServer();
        configure?.Invoke(builder);
        var app = ReferenceApplication.Build(builder);
        try
        {
            await app.StartAsync(Token);
            return app;
        }
        catch (Exception)
        {
            await app.DisposeAsync();
            throw;
        }
    }

    private sealed class RecordingLoggerProvider : ILoggerProvider
    {
        internal List<string> Lines { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Recorder(Lines);

        public void Dispose()
        {
        }

        private sealed class Recorder(List<string> lines) : ILogger
        {
            public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
                lock (lines)
                {
                    lines.Add(formatter(state, exception) + " " + exception);
                }
            }
        }
    }
}

/// <summary>Creates an isolated temporary directory for one test and removes it on dispose.</summary>
internal sealed class TemporaryDirectory : IDisposable
{
    private TemporaryDirectory(string path)
    {
        Path = path;
        Directory.CreateDirectory(path);
    }

    internal string Path { get; }

    internal string DatabasePath => System.IO.Path.Combine(Path, "reference.db");

    internal static TemporaryDirectory Create() => new(System.IO.Path.Combine(
        ResolveLinks(System.IO.Path.GetTempPath()),
        $"sm-reference-sqlite-{Guid.NewGuid():N}"));

    /// <summary>
    /// Resolves every symbolic link on the way to <paramref name="path"/>. The SQLite file target
    /// contract refuses linked paths, and the system temporary directory is reached through a link
    /// on macOS, so a test path has to be the real one.
    /// </summary>
    private static string ResolveLinks(string path)
    {
        var root = System.IO.Path.GetPathRoot(path);
        if (string.IsNullOrEmpty(root))
        {
            return path;
        }

        var current = root;
        foreach (var segment in path[root.Length..].Split(
            [System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar],
            StringSplitOptions.RemoveEmptyEntries))
        {
            current = System.IO.Path.Combine(current, segment);
            var entry = new DirectoryInfo(current);
            if (entry.Exists && entry.LinkTarget is not null &&
                entry.ResolveLinkTarget(returnFinalTarget: true) is { } target)
            {
                current = target.FullName;
            }
        }

        return current;
    }

    public void Dispose()
    {
        if (Directory.Exists(Path))
        {
            Directory.Delete(Path, recursive: true);
        }
    }
}
