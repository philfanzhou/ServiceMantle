using System.Net;
using System.Net.Sockets;
using System.Transactions;
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
    public async Task Observe_OverlongDatabaseName_DoesNotReportTruncatedTargetConnectable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var requestedDatabaseName = new string('a', 64);
        await CreateRealDatabaseAsync(requestedDatabaseName[..63]);
        var target = CreateTargetConfiguration(requestedDatabaseName);

        var observation = await new PostgreSqlDatabaseTargetPreparationProvider().ObserveAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.Null(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
    }

    [Fact]
    public async Task Observe_OverlongRoleName_DoesNotReportTruncatedRoleConnectable()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var requestedRoleName = new string('u', 64);
        const string rolePassword = "truncated-role-password";
        await CreateNonCreateDbRoleAsync(requestedRoleName[..63], rolePassword);
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            BuildConnectionString("postgres", requestedRoleName, rolePassword));

        var observation = await new PostgreSqlDatabaseTargetPreparationProvider().ObserveAsync(
            target,
            TestContext.Current.CancellationToken);

        Assert.True(observation.IsServerReachable);
        Assert.Null(observation.TargetExists);
        Assert.False(observation.IsTargetConnectable);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
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
    public async Task Observe_InFlightNpgsqlCancellation_IsPropagatedSafely()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        const string targetPassword = "observe-in-flight-secret";
        using var source = new CancellationTokenSource();
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var acceptTask = listener.AcceptTcpClientAsync(TestContext.Current.CancellationToken).AsTask();
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            $"Host=127.0.0.1;Port={port};Database=app;Username=observe-app;" +
            $"Password={targetPassword};Timeout=60;Command Timeout=60");

        var observationTask = new PostgreSqlDatabaseTargetPreparationProvider()
            .ObserveAsync(target, source.Token)
            .AsTask();
        using var accepted = await acceptTask.WaitAsync(
            TimeSpan.FromSeconds(5),
            TestContext.Current.CancellationToken);
        source.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() => observationTask);

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(targetPassword, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("observe-app", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("127.0.0.1", exception.ToString(), StringComparison.Ordinal);
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
    public async Task Prepare_CreatesDatabaseOwnedByTargetApplicationRole()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var roleName = $"app_owner_{UniqueSuffix()}";
        const string rolePassword = "app-owner-password";
        var databaseName = $"owned_{UniqueSuffix()}";
        await CreateNonCreateDbRoleAsync(roleName, rolePassword);
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            BuildConnectionString(databaseName, roleName, rolePassword));
        var request = new DatabaseTargetPreparationRequest(target, AdministrativeConnectionString());

        var result = await new PostgreSqlDatabaseTargetPreparationProvider().PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.Created, result.Outcome);
        Assert.Equal(roleName, await GetDatabaseOwnerAsync(databaseName));

        await using var applicationConnection = new NpgsqlConnection(
            BuildConnectionString(databaseName, roleName, rolePassword));
        await applicationConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = applicationConnection.CreateCommand();
        command.CommandText = "CREATE TABLE owner_can_migrate (id INTEGER PRIMARY KEY)";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Prepare_DoesNotRetainDefaultPooledAdministrativeConnection()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var applicationName = $"target-preparation-{UniqueSuffix()}";
        var administrativeBuilder = new NpgsqlConnectionStringBuilder
        {
            Host = serverConnectionInfo!.Host,
            Port = serverConnectionInfo.Port,
            Database = "postgres",
            Username = serverConnectionInfo.Username,
            Password = serverConnectionInfo.Password,
            ApplicationName = applicationName
        };
        Assert.True(administrativeBuilder.Pooling);
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration($"no_pool_{UniqueSuffix()}"),
            administrativeBuilder.ConnectionString);

        var result = await new PostgreSqlDatabaseTargetPreparationProvider().PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(0, await CountSessionsByApplicationNameAsync(applicationName));
    }

    [Fact]
    public async Task Prepare_CreatesDatabaseOutsideAmbientTransactionScope()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"ambient_transaction_{UniqueSuffix()}";
        var administrativeConnectionString = AdministrativeConnectionString();
        Assert.True(new NpgsqlConnectionStringBuilder(administrativeConnectionString).Enlist);
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            administrativeConnectionString);

        DatabaseTargetPreparationResult result;
        using (var scope = new TransactionScope(TransactionScopeAsyncFlowOption.Enabled))
        {
            Assert.NotNull(Transaction.Current);
            result = await new PostgreSqlDatabaseTargetPreparationProvider().PrepareAsync(
                request,
                TimeSpan.FromSeconds(15),
                TestContext.Current.CancellationToken);
            scope.Complete();
        }

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

    /// <summary>
    /// On a LATIN1 server a non-ASCII name can be legal server-side while being unreachable
    /// through Npgsql, whose startup packet always carries UTF-8 bytes that never match the
    /// stored LATIN1 form. Preparation must reject such a name before creating anything.
    /// </summary>
    [Fact]
    public async Task Prepare_OnLatin1Server_RejectsDriverUnsupportedNamesWithoutCreatingAnything()
    {
        Assert.SkipUnless(ShouldRunPostgreSqlTests(), SkipReason);

        await using var latin1Container = new PostgreSqlBuilder(GetPostgresImage())
            .WithPassword("latin1-password")
            .WithUsername("latin1-admin")
            .WithEnvironment("POSTGRES_INITDB_ARGS", "--encoding=LATIN1 --locale=C")
            .Build();
        await latin1Container.StartAsync(TestContext.Current.CancellationToken);
        var latin1Connection = new NpgsqlConnectionStringBuilder(latin1Container.GetConnectionString())
        {
            Pooling = false
        };

        // 32 'é' characters are 32 bytes under LATIN1 but 64 bytes over the wire as UTF-8.
        var databaseName = new string('é', 32);
        var targetBuilder = new NpgsqlConnectionStringBuilder(latin1Connection.ConnectionString)
        {
            Database = databaseName
        };
        var request = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                targetBuilder.ConnectionString),
            latin1Connection.ConnectionString);

        var result = await new PostgreSqlDatabaseTargetPreparationProvider().PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.Equal(0, await CountLatin1ContainerDatabasesAsync(latin1Connection, databaseName));
    }

    /// <summary>
    /// A driver-supported name on a LATIN1 server must produce a target that the application can
    /// actually observe, connect to, and migrate afterwards.
    /// </summary>
    [Fact]
    public async Task Prepare_OnLatin1Server_CreatesObservableConnectableTarget_ForDriverSupportedNames()
    {
        Assert.SkipUnless(ShouldRunPostgreSqlTests(), SkipReason);

        await using var latin1Container = new PostgreSqlBuilder(GetPostgresImage())
            .WithPassword("latin1-password")
            .WithUsername("latin1-admin")
            .WithEnvironment("POSTGRES_INITDB_ARGS", "--encoding=LATIN1 --locale=C")
            .Build();
        await latin1Container.StartAsync(TestContext.Current.CancellationToken);
        var latin1Connection = new NpgsqlConnectionStringBuilder(latin1Container.GetConnectionString())
        {
            Pooling = false
        };

        var databaseName = $"latin1_ascii_{UniqueSuffix()}";
        var targetBuilder = new NpgsqlConnectionStringBuilder(latin1Connection.ConnectionString)
        {
            Database = databaseName
        };
        var target = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            targetBuilder.ConnectionString);
        var provider = new PostgreSqlDatabaseTargetPreparationProvider();

        var result = await provider.PrepareAsync(
            new DatabaseTargetPreparationRequest(target, latin1Connection.ConnectionString),
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.Created, result.Outcome);

        var observation = await provider.ObserveAsync(target, TestContext.Current.CancellationToken);
        Assert.True(observation.IsServerReachable);
        Assert.True(observation.TargetExists);
        Assert.True(observation.IsTargetConnectable);
        Assert.Null(observation.ErrorCode);

        await using var applicationConnection = new NpgsqlConnection(targetBuilder.ConnectionString);
        await applicationConnection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = applicationConnection.CreateCommand();
        command.CommandText = "CREATE TABLE latin1_prepared_target (id INTEGER PRIMARY KEY)";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
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

    [Fact]
    public async Task Prepare_PreExistingDifferentlyOwnedDatabase_ReturnsTargetConflictWithoutTouchingIt()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var otherOwner = $"other_owner_{UniqueSuffix()}";
        const string otherPassword = "other-owner-password";
        await CreateNonCreateDbRoleAsync(otherOwner, otherPassword);
        var databaseName = $"foreign_{UniqueSuffix()}";
        await CreateRealDatabaseOwnedAsync(databaseName, otherOwner);
        await InsertMarkerRowAsync(databaseName, "do-not-touch");

        var provider = new PostgreSqlDatabaseTargetPreparationProvider();
        var request = new DatabaseTargetPreparationRequest(
            CreateTargetConfiguration(databaseName),
            AdministrativeConnectionString());

        var result = await provider.PrepareAsync(
            request,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, result.ErrorCode);
        Assert.Equal(otherOwner, await GetDatabaseOwnerAsync(databaseName));
        Assert.Equal("do-not-touch", await ReadMarkerRowAsync(databaseName));

        // The actual owner resolving the same target is a legitimate AlreadyExists.
        var ownedRequest = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                BuildConnectionString(databaseName, otherOwner, otherPassword)),
            AdministrativeConnectionString());

        var ownedResult = await provider.PrepareAsync(
            ownedRequest,
            TimeSpan.FromSeconds(15),
            TestContext.Current.CancellationToken);

        Assert.True(ownedResult.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, ownedResult.Outcome);
        Assert.Equal("do-not-touch", await ReadMarkerRowAsync(databaseName));
    }

    /// <summary>
    /// Deterministic proof that a concurrent race between two different owners never reports
    /// success for the loser: exactly one caller creates the database and the other observes the
    /// differently-owned target as TargetConflict.
    /// </summary>
    [Fact]
    public async Task Prepare_ConcurrentCreationWithDifferentOwners_WinnerCreates_LoserReportsConflict()
    {
        Assert.SkipUnless(CanRun, SkipReason);

        var databaseName = $"race_owner_{UniqueSuffix()}";
        const string rolePassword = "race-role-password";
        var ownerA = $"race_a_{UniqueSuffix()}";
        var ownerB = $"race_b_{UniqueSuffix()}";
        await CreateNonCreateDbRoleAsync(ownerA, rolePassword);
        await CreateNonCreateDbRoleAsync(ownerB, rolePassword);

        var testToken = TestContext.Current.CancellationToken;
        var barrier = new AsyncArrivalBarrier(2);
        var requestA = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                BuildConnectionString(databaseName, ownerA, rolePassword)),
            AdministrativeConnectionString());
        var requestB = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                BuildConnectionString(databaseName, ownerB, rolePassword)),
            AdministrativeConnectionString());

        var taskA = new PostgreSqlDatabaseTargetPreparationProvider(
            new NpgsqlBootstrapProbe(),
            new NpgsqlDatabaseCreationProbe(barrier.SignalAndWaitAsync))
            .PrepareAsync(requestA, TimeSpan.FromSeconds(20), testToken).AsTask();
        var taskB = new PostgreSqlDatabaseTargetPreparationProvider(
            new NpgsqlBootstrapProbe(),
            new NpgsqlDatabaseCreationProbe(barrier.SignalAndWaitAsync))
            .PrepareAsync(requestB, TimeSpan.FromSeconds(20), testToken).AsTask();

        await Task.WhenAll(taskA, taskB).WaitAsync(TimeSpan.FromSeconds(25), testToken);

        var resultA = await taskA;
        var resultB = await taskB;

        var createdCount = 0;
        var conflictCount = 0;
        var winner = string.Empty;

        if (resultA.Succeeded)
        {
            createdCount++;
            winner = ownerA;
        }
        else if (resultA.ErrorCode == WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict)
        {
            conflictCount++;
        }
        else
        {
            Assert.Fail($"Unexpected outcome for first caller: {resultA}");
        }

        if (resultB.Succeeded)
        {
            createdCount++;
            winner = ownerB;
        }
        else if (resultB.ErrorCode == WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict)
        {
            conflictCount++;
        }
        else
        {
            Assert.Fail($"Unexpected outcome for second caller: {resultB}");
        }

        Assert.Equal(1, createdCount);
        Assert.Equal(1, conflictCount);
        Assert.Equal(winner, await GetDatabaseOwnerAsync(databaseName));
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

    private async Task CreateRealDatabaseOwnedAsync(string databaseName, string ownerRole)
    {
        await using var connection = new NpgsqlConnection(AdministrativeConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            $"CREATE DATABASE \"{databaseName.Replace("\"", "\"\"", StringComparison.Ordinal)}\" " +
            $"OWNER \"{ownerRole.Replace("\"", "\"\"", StringComparison.Ordinal)}\"";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private async Task<int> CountLatin1ContainerDatabasesAsync(
        NpgsqlConnectionStringBuilder latin1Connection,
        string databaseName)
    {
        await using var connection = new NpgsqlConnection(latin1Connection.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM pg_database WHERE datname = @name OR datname LIKE @accentPrefix";
        command.Parameters.AddWithValue("@name", databaseName);
        command.Parameters.AddWithValue("@accentPrefix", databaseName[..1] + "%");

        var result = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return result is long count ? (int)count : 0;
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

    private async Task<string?> GetDatabaseOwnerAsync(string databaseName)
    {
        await using var connection = new NpgsqlConnection(AdministrativeConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT pg_get_userbyid(datdba) FROM pg_database WHERE datname = @name";
        command.Parameters.AddWithValue("@name", databaseName);

        return (await command.ExecuteScalarAsync(TestContext.Current.CancellationToken))?.ToString();
    }

    private async Task<int> CountSessionsByApplicationNameAsync(string applicationName)
    {
        await using var connection = new NpgsqlConnection(AdministrativeConnectionString());
        await connection.OpenAsync(TestContext.Current.CancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM pg_stat_activity WHERE application_name = @name";
        command.Parameters.AddWithValue("@name", applicationName);

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
