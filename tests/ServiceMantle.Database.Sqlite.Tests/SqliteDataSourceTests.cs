using Microsoft.Data.Sqlite;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Sqlite;
using Xunit;

namespace ServiceMantle.Database.Sqlite.Tests;

public sealed class SqliteDataSourceTests : IDisposable
{
    private readonly string baseDirectory = Path.Combine(
        AppContext.BaseDirectory, "sqlite-data-source-tests", Guid.NewGuid().ToString("N"));
    private readonly string originalCurrentDirectory = Directory.GetCurrentDirectory();

    public SqliteDataSourceTests()
    {
        // The base directory itself is never created: resolution must not touch the file system.
        Directory.CreateDirectory(baseDirectory);
        Directory.SetCurrentDirectory(Path.GetTempPath());
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(originalCurrentDirectory);
        Directory.Delete(baseDirectory, recursive: true);
    }

    [Theory]
    [InlineData("app.db", "app.db")]
    [InlineData("nested/deep/app.db", "nested/deep/app.db")]
    [InlineData("../app.db", "../app.db")]
    public void Relative_data_source_resolves_against_the_base_directory_not_the_working_directory(
        string relative,
        string expectedRelative)
    {
        var resolved = SqliteDataSource.ResolveConnectionString(
            $"Data Source={relative};Cache=Private", baseDirectory);

        var builder = new SqliteConnectionStringBuilder(resolved);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(baseDirectory, expectedRelative)),
            builder.DataSource);
        // Other parameters are preserved semantically.
        Assert.Equal(SqliteCacheMode.Private, builder.Cache);
    }

    [Fact]
    public void Resolution_depends_on_the_base_directory_not_the_process_working_directory()
    {
        var resolved = SqliteDataSource.ResolveConnectionString(
            "Data Source=app.db", baseDirectory);
        var fromAnotherWorkingDirectory = SqliteDataSource.ResolveConnectionString(
            "Data Source=app.db", baseDirectory);
        Directory.SetCurrentDirectory(AppContext.BaseDirectory);
        var unaffected = SqliteDataSource.ResolveConnectionString(
            "Data Source=app.db", baseDirectory);

        Assert.Equal(fromAnotherWorkingDirectory, resolved);
        Assert.Equal(unaffected, resolved);
    }

    [Fact]
    public void Absolute_data_source_is_returned_unchanged()
    {
        var absolute = Path.Combine(baseDirectory, "app.db");
        const string connectionString = "Data Source=PLACEHOLDER;Mode=ReadWriteCreate";

        var resolved = SqliteDataSource.ResolveConnectionString(
            connectionString.Replace("PLACEHOLDER", absolute), baseDirectory);

        Assert.Equal(connectionString.Replace("PLACEHOLDER", absolute), resolved);
    }

    [Theory]
    [InlineData("Data Source=:memory:")]
    [InlineData("Data Source=:MEMORY:")]
    [InlineData("Data Source=file:data/app.db?mode=ro")]
    public void In_memory_and_file_uri_data_sources_are_returned_unchanged(string connectionString)
    {
        Assert.Equal(connectionString, SqliteDataSource.ResolveConnectionString(
            connectionString, baseDirectory));
    }

    [Fact]
    public void Empty_data_source_fails_without_echoing_the_connection_string()
    {
        const string connectionString = "Data Source=;Password=secret-value";

        var exception = Assert.Throws<ArgumentException>(() =>
            SqliteDataSource.ResolveConnectionString(connectionString, baseDirectory));
        Assert.DoesNotContain("secret-value", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unparsable_connection_strings_and_relative_base_directories_fail_safely()
    {
        Assert.Throws<ArgumentException>(() =>
            SqliteDataSource.ResolveConnectionString("DataSource==broken", baseDirectory));
        Assert.Throws<ArgumentException>(() =>
            SqliteDataSource.ResolveConnectionString("Data Source=app.db", "relative/base"));
        Assert.Throws<ArgumentException>(() =>
            SqliteDataSource.ResolveConnectionString("Data Source=|DataDirectory|app.db", baseDirectory));
        Assert.Throws<ArgumentException>(() =>
            SqliteDataSource.ResolveConnectionString("Data Source=app.db", ""));
    }

    [Fact]
    public async Task Resolved_and_preanchored_data_sources_normalize_to_the_same_target_identity()
    {
        var preanchored = Path.Combine(baseDirectory, "app.db");
        var resolved = SqliteDataSource.ResolveConnectionString(
            "Data Source=app.db", baseDirectory);

        var provider = new SqliteDatabaseTargetPreparationProvider();
        var preanchoredIdentity = await provider.GetCanonicalTargetIdentityAsync(
            new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.Sqlite, null,
                $"Data Source={preanchored}"),
            CancellationToken.None);
        var resolvedIdentity = await provider.GetCanonicalTargetIdentityAsync(
            new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.Sqlite, null, resolved),
            CancellationToken.None);

        Assert.Equal(preanchoredIdentity, resolvedIdentity);
    }

    [Fact]
    public void Windows_drive_and_unc_relative_shapes_are_platform_conditional()
    {
        Assert.SkipWhen(!OperatingSystem.IsWindows(), "Windows path shapes run on Windows only.");

        var resolved = SqliteDataSource.ResolveConnectionString(
            "Data Source=data\\app.db", @"C:\content-root");
        Assert.Equal(
            @"C:\content-root\data\app.db",
            new SqliteConnectionStringBuilder(resolved).DataSource);
    }

    [Fact]
    public void Unix_relative_shapes_resolve_on_unix()
    {
        Assert.SkipWhen(OperatingSystem.IsWindows(), "Unix path shapes run on Unix only.");

        var resolved = SqliteDataSource.ResolveConnectionString(
            "Data Source=data/app.db", "/content-root");
        Assert.Equal(
            "/content-root/data/app.db",
            new SqliteConnectionStringBuilder(resolved).DataSource);
    }
}
