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
    public async Task Observe_AuthenticationFailure_DoesNotClaimMissingTargetExists()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"missing_auth_{UniqueSuffix()}";
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            BuildConnectionString(databaseName, serverConnectionInfo!.Username!, "wrong-password"));

        var observation = await new PostgreSqlDatabaseTargetPreparationProvider().ObserveAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.Null(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed, observation.ErrorCode);
        Assert.False(await DatabaseExistsAsync(databaseName));
    }

    [Fact]
    public async Task Observe_ExistingTargetWithoutConnectPrivilege_ReportsKnownTargetUnreachable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var roleName = $"observe_role_{UniqueSuffix()}";
        const string rolePassword = "observe-role-password";
        var databaseName = $"no_connect_{UniqueSuffix()}";
        await CreateNonCreateDbRoleAsync(roleName, rolePassword);
        await CreateRealDatabaseAsync(databaseName);
        await RevokePublicConnectAsync(databaseName);

        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            BuildConnectionString(databaseName, roleName, rolePassword));

        var observation = await new PostgreSqlDatabaseTargetPreparationProvider().ObserveAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.True(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied, observation.ErrorCode);
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

        var databaseName = $"created_\"_数据库_{UniqueSuffix()}";
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
    public async Task Prepare_RejectsOverlongDatabaseNameWithoutCreatingTruncatedTarget()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = new string('a', 64);
        var truncatedName = databaseName[..63];
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            AdministrativeConnectionString());

        var result = await new PostgreSqlDatabaseTargetPreparationProvider().PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.False(await DatabaseExistsAsync(databaseName));
        Assert.False(await DatabaseExistsAsync(truncatedName));
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
    /// Deterministic proof that both calls observe the target as missing before either issues
    /// CREATE DATABASE. Exactly one caller creates it and the racing caller reports AlreadyExists.
    /// </summary>
    [Fact]
    public async Task Prepare_ConcurrentCreation_BothSucceed_DatabaseCreatedExactlyOnce()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"concurrent_{UniqueSuffix()}";
        var testToken = TestContext.Current.CancellationToken;
        var barrier = new AsyncArrivalBarrier(2);
        var provider1 = new PostgreSqlDatabaseTargetPreparationProvider(
            new NpgsqlBootstrapProbe(),
            new NpgsqlDatabaseCreationProbe(barrier.SignalAndWaitAsync));
        var provider2 = new PostgreSqlDatabaseTargetPreparationProvider(
            new NpgsqlBootstrapProbe(),
            new NpgsqlDatabaseCreationProbe(barrier.SignalAndWaitAsync));

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

        Assert.Equal(
            1,
            new[] { result1.Outcome, result2.Outcome }
                .Count(outcome => outcome == DatabaseTargetPreparationOutcome.Created));
        Assert.Equal(
            1,
            new[] { result1.Outcome, result2.Outcome }
                .Count(outcome => outcome == DatabaseTargetPreparationOutcome.AlreadyExists));

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
    public async Task Prepare_AuthenticationFailure_IsClassifiedWithoutLeakingCredentials()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        const string wrongPassword = "administrative-auth-secret";
        var databaseName = $"auth_failed_{UniqueSuffix()}";
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            BuildConnectionString("postgres", serverConnectionInfo!.Username!, wrongPassword));

        var result = await new PostgreSqlDatabaseTargetPreparationProvider().PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed, result.ErrorCode);
        Assert.DoesNotContain(wrongPassword, result.ToString(), StringComparison.Ordinal);
        Assert.False(await DatabaseExistsAsync(databaseName));
    }

    [Fact]
    public async Task Prepare_InFlightNpgsqlCancellation_IsSafeAndDoesNotCreateTarget()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        const string administrativePassword = "in-flight-cancellation-secret";
        using var source = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();

        var databaseName = $"cancelled_{UniqueSuffix()}";
        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            $"Host=127.0.0.1;Port={port};Database=postgres;Username=cancel-admin;" +
            $"Password={administrativePassword};Timeout=60;Command Timeout=60;Pooling=false");

        var preparationTask = provider.PrepareAsync(request, TimeSpan.FromSeconds(30), source.Token).AsTask();
        using var accepted = await acceptTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        source.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => preparationTask);

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(administrativePassword, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("cancel-admin", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", exception.ToString(), StringComparison.Ordinal);
        Assert.False(await DatabaseExistsAsync(databaseName));
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

    private async Task RevokePublicConnectAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(AdministrativeConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"REVOKE CONNECT ON DATABASE \"{databaseName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" FROM PUBLIC";
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

    private sealed class AsyncArrivalBarrier
    {
        private readonly int participantCount;
        private readonly TaskCompletionSource allArrived =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int arrivals;

        public AsyncArrivalBarrier(int participantCount)
        {
            this.participantCount = participantCount;
        }

        public async ValueTask SignalAndWaitAsync(CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref arrivals) == participantCount)
            {
                allArrived.TrySetResult();
            }

            await allArrived.Task.WaitAsync(cancellationToken);
        }
    }
}
