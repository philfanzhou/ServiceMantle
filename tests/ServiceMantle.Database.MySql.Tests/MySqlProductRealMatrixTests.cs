using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MySql.Migration;
using ServiceMantle.Migration;
using ServiceMantle.Testing;
using Testcontainers.MariaDb;
using Testcontainers.MySql;
using Xunit;

namespace ServiceMantle.Database.MySql.Tests;

[RealDatabaseTest(RealDatabaseProvider.MySql)]
public sealed class MySqlProductRealMatrixTests
{
    [Fact]
    public Task MySQL_8_0_covers_bootstrap_observe_prepare_and_migration_lock() =>
        RunMatrixCaseAsync("mysql:8.0", supported: true);

    [Fact]
    public Task MySQL_8_4_covers_bootstrap_observe_prepare_and_migration_lock() =>
        RunMatrixCaseAsync("mysql:8.4", supported: true);

    [Fact]
    public Task MariaDB_10_11_is_rejected_before_creation_or_named_lock() =>
        RunMatrixCaseAsync("mariadb:10.11", supported: false);

    [Fact]
    public Task MariaDB_11_4_is_rejected_before_creation_or_named_lock() =>
        RunMatrixCaseAsync("mariadb:11.4", supported: false);

    private static async Task RunMatrixCaseAsync(
        string image,
        bool supported)
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.MySql))
        {
            RealDatabaseTestEnvironment.RequireAvailable(RealDatabaseProvider.MySql, false);
            return;
        }

        if (image.StartsWith("mariadb:", StringComparison.Ordinal))
        {
            await using var container = new MariaDbBuilder(image)
                .WithDatabase("servicemantle")
                .WithUsername("servicemantle")
                .WithPassword("test-password")
                .Build();
            await container.StartAsync(TestContext.Current.CancellationToken);
            await AssertMatrixCaseAsync(container.GetConnectionString(), supported);
            return;
        }

        await using var mysqlContainer = new MySqlBuilder(image)
            .WithDatabase("servicemantle")
            .WithUsername("servicemantle")
            .WithPassword("test-password")
            .Build();
        await mysqlContainer.StartAsync(TestContext.Current.CancellationToken);
        await AssertMatrixCaseAsync(mysqlContainer.GetConnectionString(), supported);
    }

    private static async Task AssertMatrixCaseAsync(
        string connectionString,
        bool supported)
    {
        var administrative = new MySqlConnectionStringBuilder(connectionString)
        {
            UserID = "root",
            Database = string.Empty,
            Pooling = false,
            AutoEnlist = false
        };
        var missingDatabase = $"sm_matrix_{Guid.NewGuid():N}";
        var missingTarget = new MySqlConnectionStringBuilder(administrative.ConnectionString)
        {
            Database = missingDatabase
        };
        var missingBootstrap = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.MySql,
            "8.4",
            missingTarget.ConnectionString);

        var validation = await new MySqlBootstrapDatabaseProvider().ValidateAsync(
            missingBootstrap,
            TestContext.Current.CancellationToken);
        var observation = await new MySqlDatabaseTargetPreparationProvider().ObserveAsync(
            missingBootstrap,
            TestContext.Current.CancellationToken);
        var preparation = await new MySqlDatabaseTargetPreparationProvider().PrepareAsync(
            new DatabaseTargetPreparationRequest(missingBootstrap, administrative.ConnectionString),
            TimeSpan.FromSeconds(30),
            TestContext.Current.CancellationToken);

        if (supported)
        {
            Assert.False(validation.IsValid);
            Assert.Equal("database.target_not_found", validation.ErrorCode);
            Assert.Equal(DatabaseTargetObservationStatus.TargetMissing, observation.Status);
            Assert.True(preparation.Succeeded);
            Assert.Equal(DatabaseTargetPreparationOutcome.Created, preparation.Outcome);
        }
        else
        {
            Assert.False(validation.IsValid);
            Assert.Equal("database.provider_validation_failed", validation.ErrorCode);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, preparation.ErrorCode);
        }

        Assert.Equal(supported, await DatabaseExistsAsync(administrative, missingDatabase));

        var existingTarget = new MySqlConnectionStringBuilder(administrative.ConnectionString)
        {
            Database = "servicemantle"
        };
        var lockBootstrap = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.MySql,
            "8.4",
            existingTarget.ConnectionString);
        var serviceId = ServiceId.Parse($"mysql-matrix-{Guid.NewGuid():N}");
        var lockName = MySqlMigrationLockName.Derive(serviceId);
        var lockProvider = new MySqlMigrationLockProvider();

        if (supported)
        {
            var lease = await lockProvider.AcquireAsync(
                serviceId,
                lockBootstrap,
                TimeSpan.FromSeconds(10),
                TestContext.Current.CancellationToken);
            Assert.NotNull(await LockOwnerAsync(administrative, lockName));
            await lease.DisposeAsync();
            Assert.Null(await LockOwnerAsync(administrative, lockName));
            await DropDatabaseAsync(administrative, missingDatabase);
        }
        else
        {
            var exception = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
                await lockProvider.AcquireAsync(
                    serviceId,
                    lockBootstrap,
                    TimeSpan.FromSeconds(10),
                    TestContext.Current.CancellationToken));
            Assert.Equal(WellKnownMigrationErrorCodes.LockFailed, exception.ErrorCode);
            Assert.Null(await LockOwnerAsync(administrative, lockName));
        }
    }

    private static async Task<bool> DatabaseExistsAsync(
        MySqlConnectionStringBuilder administrative,
        string databaseName)
    {
        await using var connection = new MySqlConnection(administrative.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM INFORMATION_SCHEMA.SCHEMATA WHERE BINARY SCHEMA_NAME = BINARY @name";
        command.Parameters.AddWithValue("@name", databaseName);
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1;
    }

    private static async Task<object?> LockOwnerAsync(
        MySqlConnectionStringBuilder administrative,
        string lockName)
    {
        await using var connection = new MySqlConnection(administrative.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT IS_USED_LOCK(@name)";
        command.Parameters.AddWithValue("@name", lockName);
        var value = await command.ExecuteScalarAsync(TestContext.Current.CancellationToken);
        return value is DBNull ? null : value;
    }

    private static async Task DropDatabaseAsync(
        MySqlConnectionStringBuilder administrative,
        string databaseName)
    {
        await using var connection = new MySqlConnection(administrative.ConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = $"DROP DATABASE {MySqlDatabaseTarget.QuoteIdentifier(databaseName)}";
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }
}
