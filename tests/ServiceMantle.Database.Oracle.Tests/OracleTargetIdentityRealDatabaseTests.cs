using System.Data;
using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle;
using ServiceMantle.Database.Oracle.Migration;
using ServiceMantle.Migration;
using ServiceMantle.Testing;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

/// <summary>
/// Exercises the target-identity classification row on the pinned Oracle Database Free
/// environment: a CREATE SESSION-only local user is a valid target everywhere, while an
/// Oracle-maintained user and a no-prefix common user are refused before any target DDL or lock
/// allocation, and the administrative identity keeps its existing support.
/// </summary>
/// <remarks>
/// The no-prefix common user requires <c>COMMON_USER_PREFIX = ''</c>, applied by
/// <c>eng/oracle-identity-fixture.sql</c> in CI and by the same script locally; its connection is
/// taken from <c>SERVICEMANTLE_ORACLE_ROOT_CONNECTION_STRING</c> (SYSTEM on the root container).
/// </remarks>
[RealDatabaseTest(RealDatabaseProvider.Oracle)]
public sealed class OracleTargetIdentityRealDatabaseTests
{
    private const string AdminConnectionVariable = "SERVICEMANTLE_ORACLE_ADMIN_CONNECTION_STRING";
    private const string RootConnectionVariable = "SERVICEMANTLE_ORACLE_ROOT_CONNECTION_STRING";
    private const string TargetPassword = "Identity-Real-1";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task E1_A_create_session_only_local_user_is_a_valid_target_everywhere()
    {
        var admin = RequireAdminConnectionString();
        var user = NewIdentifier("SM_ID_LOCAL");
        await CreateUserAsync(admin, user);
        var missing = NewIdentifier("SM_ID_MISS");
        try
        {
            // Bootstrap validation succeeds.
            var validation = await new OracleBootstrapDatabaseProvider().ValidateAsync(
                Configuration(admin, user), Token);
            Assert.True(validation.IsValid);

            // Observation reports the target connectable.
            var observation = await new OracleDatabaseTargetPreparationProvider().ObserveAsync(
                Configuration(admin, user), Token);
            Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable, observation.Status);

            // Prepare answers AlreadyExists for the present user and Created for a missing one.
            var existing = await new OracleDatabaseTargetPreparationProvider().PrepareAsync(
                Request(admin, user), TimeSpan.FromSeconds(30), Token);
            Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, existing.Outcome);
            var created = await new OracleDatabaseTargetPreparationProvider().PrepareAsync(
                Request(admin, missing), TimeSpan.FromSeconds(30), Token);
            Assert.Equal(DatabaseTargetPreparationOutcome.Created, created.Outcome);

            // The migration lock is acquirable with only CREATE SESSION plus DBMS_LOCK.
            await using var lease = await new OracleMigrationLockProvider().AcquireAsync(
                ServiceId.Parse($"identity-local-{Guid.NewGuid():N}"),
                Bootstrap(admin, user), TimeSpan.FromSeconds(10), Token);
            Assert.False(lease.LeaseLost.IsCancellationRequested);
        }
        finally
        {
            await DropUserIfPresentAsync(admin, user);
            await DropUserIfPresentAsync(admin, missing);
        }
    }

    [Fact]
    public async Task E2_An_oracle_maintained_local_user_target_is_refused_before_any_ddl_or_lock()
    {
        var admin = RequireAdminConnectionString();
        var user = NewIdentifier("SM_ID_MAINT");
        const string password = "Maintained-Real-1";
        // Created with _ORACLE_SCRIPT so the row classifies ORACLE_MAINTAINED = 'Y' while
        // staying a local PDB user with no C## prefix.
        await DropUserIfPresentAsync(admin, user);
        await ExecuteAdminAsync(
            admin,
            $"""
            BEGIN
                EXECUTE IMMEDIATE 'ALTER SESSION SET "_ORACLE_SCRIPT"=TRUE';
                EXECUTE IMMEDIATE 'CREATE USER "{user}" IDENTIFIED BY "{password}"';
                EXECUTE IMMEDIATE 'GRANT CREATE SESSION TO "{user}"';
            END;
            """);
        try
        {
            await AssertRefusedTargetAsync(admin, user, password);
        }
        finally
        {
            // An Oracle-maintained user is only droppable from a session carrying the same
            // hidden flag that created it.
            await ExecuteAdminAsync(
                admin,
                $"""
                BEGIN
                    EXECUTE IMMEDIATE 'ALTER SESSION SET "_ORACLE_SCRIPT"=TRUE';
                    EXECUTE IMMEDIATE 'DROP USER "{user}" CASCADE';
                END;
                """);
        }
    }

    [Fact]
    public async Task E2_System_as_the_target_is_refused_before_any_ddl_or_lock()
    {
        var admin = RequireAdminConnectionString();
        var password = new OracleConnectionStringBuilder(admin).Password;

        await AssertRefusedTargetAsync(admin, "SYSTEM", password);
    }

    [Fact]
    public async Task E3_A_no_prefix_common_user_target_is_refused_without_any_ddl()
    {
        var admin = RequireAdminConnectionString();
        var root = RequireRootConnectionString();
        var user = NewIdentifier("SM_ID_COMMON");
        // A common user created with CONTAINER=ALL from the root; the empty COMMON_USER_PREFIX
        // (the CI identity fixture) is what makes this name legal without the C## prefix.
        await ExecuteAsync(root, $"""CREATE USER "{user}" IDENTIFIED BY "{TargetPassword}" CONTAINER=ALL""");
        await ExecuteAsync(root, $"GRANT CREATE SESSION TO \"{user}\" CONTAINER=ALL");
        try
        {
            const string classificationQuery = "SELECT common || '|' || to_char(created, 'YYYY-MM-DD') FROM all_users WHERE username = '{0}'";
            var before = Assert.Single(await ReadStringsAsync(
                root, string.Format(System.Globalization.CultureInfo.InvariantCulture, classificationQuery, user)));
            await AssertRefusedTargetAsync(admin, user, TargetPassword);

            // The refusal preceded any target DDL: the user row is unchanged.
            var prepared = await new OracleDatabaseTargetPreparationProvider().PrepareAsync(
                Request(admin, user), TimeSpan.FromSeconds(30), Token);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, prepared.ErrorCode);
            var after = Assert.Single(await ReadStringsAsync(
                root, string.Format(System.Globalization.CultureInfo.InvariantCulture, classificationQuery, user)));
            Assert.Equal(before, after);
        }
        finally
        {
            await ExecuteAsync(root, $"""DROP USER "{user}" CASCADE""");
        }
    }

    private async Task AssertRefusedTargetAsync(string admin, string user, string password)
    {
        // Bootstrap validation fails with the fixed invalid-connection classification.
        var validation = await new OracleBootstrapDatabaseProvider().ValidateAsync(
            Configuration(admin, user, password), Token);
        Assert.False(validation.IsValid);
        Assert.Equal("database.connection_string_invalid", validation.ErrorCode);

        // Observation answers TargetUnreachable(InvalidTarget) with the existence evidence kept.
        var observation = await new OracleDatabaseTargetPreparationProvider().ObserveAsync(
            Configuration(admin, user, password), Token);
        Assert.Equal(DatabaseTargetObservationStatus.TargetUnreachable, observation.Status);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, observation.ErrorCode);
        Assert.True(observation.TargetExists);

        // The migration lock is not supported and allocates nothing.
        var serviceId = ServiceId.Parse($"identity-refused-{Guid.NewGuid():N}");
        var lockFailure = await Assert.ThrowsAsync<DatabaseMigrationLockException>(async () =>
            await new OracleMigrationLockProvider().AcquireAsync(
                serviceId, Bootstrap(admin, user, password), TimeSpan.FromSeconds(10), Token));
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, lockFailure.ErrorCode);
        var lockName = OracleMigrationLockName.Derive(serviceId);
        var allocated = await ReadStringsAsync(
            CreateSysDbaConnectionString(admin),
            $"SELECT name FROM sys.dbms_lock_allocated WHERE name = '{lockName}'");
        Assert.Empty(allocated);

        // No target identity reaches any projection.
        Assert.DoesNotContain(user, validation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(password, validation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(user, observation.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(user, lockFailure.ToString(), StringComparison.Ordinal);
    }

    // --- helpers --------------------------------------------------------------------------------

    private static string RequireAdminConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(AdminConnectionVariable);
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.Oracle,
            !string.IsNullOrWhiteSpace(value));
        return new OracleConnectionStringBuilder(value!)
        {
            Pooling = false,
            Enlist = "false",
        }.ConnectionString;
    }

    private static string RequireRootConnectionString()
    {
        var admin = RequireAdminConnectionString();
        var value = Environment.GetEnvironmentVariable(RootConnectionVariable);
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.Oracle,
            !string.IsNullOrWhiteSpace(value));
        return new OracleConnectionStringBuilder(value!)
        {
            Pooling = false,
            Enlist = "false",
        }.ConnectionString;
    }

    private static BootstrapDatabaseConfiguration Configuration(
        string admin,
        string user,
        string? password = null) =>
        new(
            WellKnownDatabaseProviderIds.Oracle,
            "23.26.1.0",
            WithIdentity(admin, user, password));

    private static BootstrapDatabaseConfiguration Bootstrap(
        string admin,
        string user,
        string? password = null) => Configuration(admin, user, password);

    private static DatabaseTargetPreparationRequest Request(string admin, string user) =>
        new(Configuration(admin, user), admin);

    private static string WithIdentity(string connectionString, string user, string? password)
    {
        var builder = new OracleConnectionStringBuilder(connectionString)
        {
            UserID = user,
            Password = password ?? TargetPassword,
            Pooling = false,
            Enlist = "false",
        };
        return builder.ConnectionString;
    }

    private static string NewIdentifier(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, 30)].ToUpperInvariant();

    private static async Task CreateUserAsync(string admin, string user)
    {
        await DropUserIfPresentAsync(admin, user);
        await ExecuteAdminAsync(admin, $"CREATE USER \"{user}\" IDENTIFIED BY \"{TargetPassword}\"");
        await ExecuteAdminAsync(admin, $"GRANT CREATE SESSION TO \"{user}\"");
        await ExecuteAdminAsync(
            CreateSysDbaConnectionString(admin),
            $"GRANT EXECUTE ON SYS.DBMS_LOCK TO \"{user}\"");
    }

    private static string CreateSysDbaConnectionString(string admin)
    {
        var builder = new OracleConnectionStringBuilder(admin)
        {
            UserID = "sys",
            Pooling = false,
            Enlist = "false",
        };
        builder["DBA Privilege"] = "SYSDBA";
        return builder.ConnectionString;
    }

    private static async Task<bool> UserExistsAsync(string admin, string user)
    {
        var found = await ReadStringsAsync(admin, $"SELECT username FROM all_users WHERE username = '{user}'");
        return found.Count > 0;
    }

    private static async Task DropUserIfPresentAsync(string admin, string user)
    {
        if (!await UserExistsAsync(admin, user))
        {
            return;
        }

        await ExecuteAdminAsync(admin, $"""DROP USER "{user}" CASCADE""");
    }

    private static async Task ExecuteAdminAsync(string connectionString, string statement) =>
        await ExecuteAsync(connectionString, statement);

    private static async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static async Task<List<string>> ReadStringsAsync(string connectionString, string query)
    {
        await using var connection = new OracleConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandTimeout = 30;
        command.CommandText = query;
        var values = new List<string>();
        await using var reader = await command.ExecuteReaderAsync(Token);
        while (await reader.ReadAsync(Token))
        {
            values.Add(reader.IsDBNull(0) ? string.Empty : Convert.ToString(reader.GetValue(0), System.Globalization.CultureInfo.InvariantCulture)!);
        }

        return values;
    }
}
