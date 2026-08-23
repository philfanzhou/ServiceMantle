using System.Net;
using System.Net.Sockets;
using Npgsql;
using ServiceMantle.Bootstrap;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.Database.PostgreSql.Tests;

/// <summary>
/// Real PostgreSQL database target preparation tests using Testcontainers.
/// Enabled via RUN_SERVICEMANTLE_POSTGRES_TESTS=true and docker availability.
/// </summary>
public sealed class PostgreSqlDatabaseTargetPreparationConcurrencyTests : IAsyncLifetime
{
    private PostgreSqlContainer? container;
    private NpgsqlConnectionStringBuilder? serverConnectionInfo;

    public async ValueTask InitializeAsync()
    {
        if (!ShouldRunPostgreSqlTests())
        {
            return;
        }

        var image = GetPostgresImage();
        container = new PostgreSqlBuilder(image)
            .WithPassword("target-prep-password")
            .WithUsername("target-prep-admin")
            .Build();

        await container.StartAsync(TestContext.Current.CancellationToken);
        serverConnectionInfo = new NpgsqlConnectionStringBuilder(container.GetConnectionString());
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
    public async Task Observe_MissingTarget_ReportsServerReachableAndTargetMissing()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var target = CreateTargetConfiguration($"missing_{UniqueSuffix()}");

        var observation = await provider.ObserveAsync(target, TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.False(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Null(observation.ErrorCode);
    }

    [Fact]
    public async Task Observe_ExistingTarget_ReportsTargetConnectable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"existing_{UniqueSuffix()}";
        await CreateRealDatabaseAsync(databaseName);

        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var target = CreateTargetConfiguration(databaseName);

        var observation = await provider.ObserveAsync(target, TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.True(observation.TargetExists);
        Assert.True(observation.IsTargetConnectable);
        Assert.Null(observation.ErrorCode);
    }

    [Fact]
    public async Task Observe_UnreachableServer_ReportsServerUnreachable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var unreachable = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            "Host=127.0.0.1;Port=1;Database=whatever;Username=nobody;Password=nothing;Timeout=2");

        var observation = await provider.ObserveAsync(unreachable, TestContext.Current.CancellationToken);

        Assert.False(observation.IsServerReachable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed, observation.ErrorCode);
    }

    [Fact]
    public async Task Prepare_CreatesDatabase_WhenMissing()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"created_{UniqueSuffix()}";
        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            AdministrativeConnectionString());

        var result = await provider.PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.Created, result.Outcome);
        Assert.True(await DatabaseExistsAsync(databaseName));
    }

    [Fact]
    public async Task Prepare_ExistingDatabase_ReturnsAlreadyExists_AndDoesNotModifyData()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"protected_{UniqueSuffix()}";
        await CreateRealDatabaseAsync(databaseName);
        await InsertMarkerRowAsync(databaseName, "do-not-touch");

        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            AdministrativeConnectionString());

        var result = await provider.PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, result.Outcome);
        Assert.Equal("do-not-touch", await ReadMarkerRowAsync(databaseName));
    }

    /// <summary>
    /// Deterministic proof that two concurrent Prepare calls for the same missing database never
    /// result in a destructive failure: exactly one database ends up existing, and both callers
    /// observe success (one Created, and the other either Created or AlreadyExists depending on
    /// the exact race, since PostgreSQL's own duplicate_database error is treated as success).
    /// </summary>
    [Fact]
    public async Task Prepare_ConcurrentCreation_BothSucceed_DatabaseCreatedExactlyOnce()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"concurrent_{UniqueSuffix()}";
        var testToken = TestContext.Current.CancellationToken;
        var provider1 = new PostgreSqlDatabaseTargetPreparationProvider();
        var provider2 = new PostgreSqlDatabaseTargetPreparationProvider();

        var request1 = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            AdministrativeConnectionString());
        var request2 = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            AdministrativeConnectionString());

        var task1 = provider1.PrepareAsync(request1, TimeSpan.FromSeconds(20), testToken).AsTask();
        var task2 = provider2.PrepareAsync(request2, TimeSpan.FromSeconds(20), testToken).AsTask();

        await Task.WhenAll(task1, task2).WaitAsync(TimeSpan.FromSeconds(25), testToken);

        var result1 = await task1;
        var result2 = await task2;

        Assert.True(result1.Succeeded);
        Assert.True(result2.Succeeded);

        var createdCount = new[] { result1.Outcome, result2.Outcome }
            .Count(outcome => outcome == DatabaseTargetPreparationOutcome.Created);
        Assert.True(createdCount is 1 or 2);

        Assert.Equal(1, await CountDatabasesNamedAsync(databaseName));
    }

    [Fact]
    public async Task Prepare_PermissionDenied_WhenAdministrativeRoleLacksCreateDb()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var roleName = $"limited_role_{UniqueSuffix()}";
        const string rolePassword = "limited-role-password";
        await CreateNonCreateDbRoleAsync(roleName, rolePassword);

        var limitedAdministrativeConnectionString = BuildConnectionString(
            database: "postgres",
            username: roleName,
            password: rolePassword);

        var databaseName = $"denied_{UniqueSuffix()}";
        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            limitedAdministrativeConnectionString);

        var result = await provider.PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied, result.ErrorCode);
        Assert.False(await DatabaseExistsAsync(databaseName));
    }

    [Fact]
    public async Task Prepare_Cancellation_ThrowsOperationCanceledException()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        using var source = new CancellationTokenSource();
        source.Cancel();

        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration($"cancelled_{UniqueSuffix()}"),
            AdministrativeConnectionString());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            provider.PrepareAsync(request, TimeSpan.FromSeconds(15), source.Token).AsTask());
    }

    /// <summary>
    /// Deterministic real-network timeout: a bare TCP listener accepts the connection but never
    /// completes the PostgreSQL startup handshake, so the real Npgsql client genuinely hangs until
    /// the provider's own timeout cancels it. This does not depend on network routing behavior the
    /// way an unreachable external address would, so it is reproducible in any CI environment.
    /// </summary>
    [Fact]
    public async Task Prepare_Timeout_WithUnresponsiveEndpoint_ReturnsTimeoutErrorCode()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();

        try
        {
            var provider = new PostgreSqlDatabaseTargetPreparationProvider();
            var unresponsiveAdministrative =
                $"Host=127.0.0.1;Port={port};Database=postgres;Username=nobody;Password=nothing;Timeout=60;Command Timeout=60";
            var request = new DatabaseTargetPreparationRequest(
                CreateTargetConfiguration($"timeout_{UniqueSuffix()}"),
                unresponsiveAdministrative);

            var result = await provider.PrepareAsync(
                request,
                TimeSpan.FromMilliseconds(500),
                TestContext.Current.CancellationToken);

            Assert.False(result.Succeeded);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, result.ErrorCode);
        }
        finally
        {
            listener.Stop();
            try
            {
                using var accepted = await acceptTask.WaitAsync(TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
            }
            catch
            {
                // The pending accept is torn down with the listener; ignore cleanup races.
            }
        }
    }

    [Fact]
    public async Task Prepare_NoSecrets_InResultOrExceptionText()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var roleName = $"limited_role_{UniqueSuffix()}";
        const string rolePassword = "leak-check-password";
        await CreateNonCreateDbRoleAsync(roleName, rolePassword);

        var limitedAdministrativeConnectionString = BuildConnectionString(
            database: "postgres",
            username: roleName,
            password: rolePassword);

        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration($"leak_{UniqueSuffix()}"),
            limitedAdministrativeConnectionString);

        var result = await provider.PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        var text = result.ToString();
        Assert.DoesNotContain(rolePassword, text, StringComparison.Ordinal);
        Assert.DoesNotContain(serverConnectionInfo!.Password!, text, StringComparison.Ordinal);
        Assert.DoesNotContain("Password", text, StringComparison.Ordinal);
    }

    private bool CanRun => ShouldRunPostgreSqlTests() && serverConnectionInfo is not null;

    private const string SkipReason = "PostgreSQL tests disabled or container not initialized.";

    private BootstrapDatabaseConfiguration CreateTargetConfiguration(string databaseName) =>
        new(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            BuildConnectionString(databaseName, serverConnectionInfo!.Username!, serverConnectionInfo!.Password!));

    private string AdministrativeConnectionString() =>
        BuildConnectionString("postgres", serverConnectionInfo!.Username!, serverConnectionInfo!.Password!);

    private string BuildConnectionString(string database, string username, string password) =>
        new NpgsqlConnectionStringBuilder
        {
            Host = serverConnectionInfo!.Host,
            Port = serverConnectionInfo.Port,
            Database = database,
            Username = username,
            Password = password,
            Pooling = false
        }.ConnectionString;

    private async Task CreateRealDatabaseAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(AdministrativeConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = $"CREATE DATABASE \"{databaseName}\"";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task CreateNonCreateDbRoleAsync(string roleName, string password)
    {
        await using var connection = new NpgsqlConnection(AdministrativeConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE ROLE \"{roleName}\" LOGIN NOCREATEDB PASSWORD '{password}'";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task InsertMarkerRowAsync(string databaseName, string markerValue)
    {
        await using var connection = new NpgsqlConnection(
            BuildConnectionString(databaseName, serverConnectionInfo!.Username!, serverConnectionInfo!.Password!));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var createTable = connection.CreateCommand();
        createTable.CommandText = "CREATE TABLE preparation_marker (value TEXT NOT NULL)";
        await createTable.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);

        await using var insert = connection.CreateCommand();
        insert.CommandText = "INSERT INTO preparation_marker (value) VALUES (@value)";
        var parameter = insert.CreateParameter();
        parameter.ParameterName = "@value";
        parameter.Value = markerValue;
        insert.Parameters.Add(parameter);
        await insert.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<string?> ReadMarkerRowAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(
            BuildConnectionString(databaseName, serverConnectionInfo!.Username!, serverConnectionInfo!.Password!));
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM preparation_marker LIMIT 1";
        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result?.ToString();
    }

    private async Task<bool> DatabaseExistsAsync(string databaseName) =>
        await CountDatabasesNamedAsync(databaseName) > 0;

    private async Task<int> CountDatabasesNamedAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(AdministrativeConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_database WHERE datname = @name";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "@name";
        parameter.Value = databaseName;
        command.Parameters.Add(parameter);

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is long count ? (int)count : 0;
    }

    private static string UniqueSuffix() => Guid.NewGuid().ToString("N")[..12];

    private static bool ShouldRunPostgreSqlTests()
    {
        var envVar = Environment.GetEnvironmentVariable("RUN_SERVICEMANTLE_POSTGRES_TESTS");
        return envVar?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
    }

    private static string GetPostgresImage()
    {
        var envVar = Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE");
        return envVar ?? "postgres:15-alpine";
    }
}
