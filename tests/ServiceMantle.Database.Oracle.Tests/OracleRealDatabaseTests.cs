using System.Data;
using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle;
using ServiceMantle.Testing;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

/// <summary>Exercises the pinned Oracle Database Free <c>FREEPDB1</c> environment.</summary>
[RealDatabaseTest(RealDatabaseProvider.Oracle)]
public sealed class OracleRealDatabaseTests
{
    private const string AdminConnectionVariable = "SERVICEMANTLE_ORACLE_ADMIN_CONNECTION_STRING";

    [Fact]
    public async Task Real_database_environment_is_reachable()
    {
        var admin = Environment.GetEnvironmentVariable(AdminConnectionVariable);
        RealDatabaseTestEnvironment.RequireAvailable(RealDatabaseProvider.Oracle, !string.IsNullOrWhiteSpace(admin));

        await OracleListenerPreflight.VerifyAsync(async cancellationToken =>
        {
            var builder = new OracleConnectionStringBuilder(admin)
            {
                ConnectionTimeout = 8,
                Pooling = false,
                Enlist = "false"
            };
            await using var connection = new OracleConnection(builder.ConnectionString);
            await connection.OpenAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandTimeout = 5;
            command.CommandText = "SELECT 1 FROM DUAL";

            Assert.Equal(
                1M,
                Convert.ToDecimal(
                    await command.ExecuteScalarAsync(cancellationToken),
                    System.Globalization.CultureInfo.InvariantCulture));
        }, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task Missing_user_is_created_observed_and_then_adopted()
    {
        var admin = RequireAdminConnectionString();
        var user = NewUserName("SM_CREATE");
        var password = "Target-Real-1";
        await DropUserIfPresentAsync(admin, user);
        try
        {
            var provider = new OracleDatabaseTargetPreparationProvider();
            var request = CreateRequest(admin, user, password);

            var created = await provider.PrepareAsync(
                request,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var observation = await provider.ObserveAsync(
                request.Target,
                TestContext.Current.CancellationToken);
            var existing = await provider.PrepareAsync(
                request,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.Equal(DatabaseTargetPreparationOutcome.Created, created.Outcome);
            Assert.Equal(DatabaseTargetObservationStatus.TargetConnectable, observation.Status);
            Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, existing.Outcome);
        }
        finally
        {
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Fact]
    public async Task Existing_user_with_wrong_credentials_is_a_non_destructive_conflict()
    {
        var admin = RequireAdminConnectionString();
        var user = NewUserName("SM_CONFLICT");
        var password = "Target-Real-1";
        await CreateUserAsync(admin, user, password, grantCreateSession: true);
        try
        {
            var provider = new OracleDatabaseTargetPreparationProvider();
            var result = await provider.PrepareAsync(
                CreateRequest(admin, user, "Wrong-Real-2"),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            var surviving = await provider.ObserveAsync(
                CreateTarget(admin, user, password),
                TestContext.Current.CancellationToken);

            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, result.ErrorCode);
            Assert.True(surviving.IsTargetConnectable);
            Assert.DoesNotContain(password, result.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Fact]
    public async Task Create_permission_denial_fails_closed()
    {
        var systemAdmin = RequireAdminConnectionString();
        var limitedAdmin = NewUserName("SM_LIMITED");
        var target = NewUserName("SM_DENIED");
        var adminPassword = "Limited-Real-1";
        await CreateUserAsync(systemAdmin, limitedAdmin, adminPassword, grantCreateSession: true);
        try
        {
            var result = await new OracleDatabaseTargetPreparationProvider().PrepareAsync(
                CreateRequest(
                    WithIdentity(systemAdmin, target, "Target-Real-1"),
                    WithIdentity(systemAdmin, limitedAdmin, adminPassword)),
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);

            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied, result.ErrorCode);
            Assert.False(await UserExistsAsync(systemAdmin, target));
        }
        finally
        {
            await DropUserIfPresentAsync(systemAdmin, target);
            await DropUserIfPresentAsync(systemAdmin, limitedAdmin);
        }
    }

    [Fact]
    public async Task Adoption_race_survives_the_creators_post_grant_cancellation()
    {
        var admin = RequireAdminConnectionString();
        var user = NewUserName("SM_ADOPT");
        var password = "Target-Real-1";
        await DropUserIfPresentAsync(admin, user);
        using var creatorCancellation = new CancellationTokenSource();
        var grantCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCreatorProbe = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creatorOperations = new PostGrantProbeGateOperations(
            new OracleDatabaseOperations(),
            grantCompleted,
            releaseCreatorProbe);
        try
        {
            var request = CreateRequest(admin, user, password);
            var creatorTask = new OracleDatabaseTargetPreparationProvider(creatorOperations)
                .PrepareAsync(request, TimeSpan.FromSeconds(30), creatorCancellation.Token)
                .AsTask();
            await grantCompleted.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            var adopter = await new OracleDatabaseTargetPreparationProvider().PrepareAsync(
                request,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            creatorCancellation.Cancel();
            releaseCreatorProbe.TrySetResult();

            await Assert.ThrowsAsync<OperationCanceledException>(() => creatorTask);
            Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, adopter.Outcome);
            Assert.True((await new OracleDatabaseTargetPreparationProvider().ObserveAsync(
                request.Target,
                TestContext.Current.CancellationToken)).IsTargetConnectable);
        }
        finally
        {
            releaseCreatorProbe.TrySetResult();
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Fact]
    public async Task Create_grant_window_returns_conflict_without_deleting_the_other_actor_user()
    {
        var admin = RequireAdminConnectionString();
        var user = NewUserName("SM_WINDOW");
        var password = "Target-Real-1";
        await DropUserIfPresentAsync(admin, user);
        var grantReached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowGrant = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var creatorOperations = new BeforeGrantGateOperations(
            new OracleDatabaseOperations(),
            grantReached,
            allowGrant);
        try
        {
            var request = CreateRequest(admin, user, password);
            var creatorTask = new OracleDatabaseTargetPreparationProvider(creatorOperations)
                .PrepareAsync(request, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
                .AsTask();
            await grantReached.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            var contender = await new OracleDatabaseTargetPreparationProvider().PrepareAsync(
                request,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            allowGrant.TrySetResult();
            var creator = await creatorTask;

            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, contender.ErrorCode);
            Assert.Equal(DatabaseTargetPreparationOutcome.Created, creator.Outcome);
            Assert.True(await UserExistsAsync(admin, user));
        }
        finally
        {
            allowGrant.TrySetResult();
            await DropUserIfPresentAsync(admin, user);
        }
    }

    [Fact]
    public async Task Lost_grant_acknowledgement_preserves_a_concurrently_adopted_user()
    {
        var admin = RequireAdminConnectionString();
        var user = NewUserName("SM_GRANT_ACK");
        var password = "Target-Real-1";
        await DropUserIfPresentAsync(admin, user);
        var grantCommitted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseFailure = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var issuerOperations = new LostGrantAcknowledgementOperations(
            new OracleDatabaseOperations(),
            grantCommitted,
            releaseFailure);
        try
        {
            var request = CreateRequest(admin, user, password);
            var issuerTask = new OracleDatabaseTargetPreparationProvider(issuerOperations)
                .PrepareAsync(request, TimeSpan.FromSeconds(30), TestContext.Current.CancellationToken)
                .AsTask();
            await grantCommitted.Task.WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);

            var adopter = await new OracleDatabaseTargetPreparationProvider().PrepareAsync(
                request,
                TimeSpan.FromSeconds(30),
                TestContext.Current.CancellationToken);
            releaseFailure.TrySetResult();
            var issuer = await issuerTask;

            Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, adopter.Outcome);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed, issuer.ErrorCode);
            Assert.True(await UserExistsAsync(admin, user));
        }
        finally
        {
            releaseFailure.TrySetResult();
            await DropUserIfPresentAsync(admin, user);
        }
    }

    private static string RequireAdminConnectionString()
    {
        var value = Environment.GetEnvironmentVariable(AdminConnectionVariable);
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.Oracle,
            !string.IsNullOrWhiteSpace(value));
        return new OracleConnectionStringBuilder(value!)
        {
            Pooling = false,
            Enlist = "false"
        }.ConnectionString;
    }

    private static DatabaseTargetPreparationRequest CreateRequest(
        string adminConnectionString,
        string user,
        string password) =>
        CreateRequest(
            WithIdentity(adminConnectionString, user, password),
            adminConnectionString);

    private static DatabaseTargetPreparationRequest CreateRequest(
        string targetConnectionString,
        string adminConnectionString) =>
        new(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.Oracle,
                "23.26.1.0",
                targetConnectionString),
            adminConnectionString);

    private static BootstrapDatabaseConfiguration CreateTarget(
        string adminConnectionString,
        string user,
        string password) =>
        new(
            WellKnownDatabaseProviderIds.Oracle,
            "23.26.1.0",
            WithIdentity(adminConnectionString, user, password));

    private static string WithIdentity(string connectionString, string user, string password)
    {
        var builder = new OracleConnectionStringBuilder(connectionString)
        {
            UserID = user,
            Password = password,
            Pooling = false,
            Enlist = "false"
        };
        return builder.ConnectionString;
    }

    private static string NewUserName(string prefix) =>
        $"{prefix}_{Guid.NewGuid():N}"[..Math.Min(prefix.Length + 9, 30)].ToUpperInvariant();

    private static async Task CreateUserAsync(
        string adminConnectionString,
        string user,
        string password,
        bool grantCreateSession)
    {
        await DropUserIfPresentAsync(adminConnectionString, user);
        await ExecuteAdminAsync(
            adminConnectionString,
            $"CREATE USER \"{user}\" IDENTIFIED BY \"{password}\"");
        if (grantCreateSession)
        {
            await ExecuteAdminAsync(adminConnectionString, $"GRANT CREATE SESSION TO \"{user}\"");
        }
    }

    private static async Task DropUserIfPresentAsync(string adminConnectionString, string user)
    {
        if (!await UserExistsAsync(adminConnectionString, user))
        {
            return;
        }

        await ExecuteAdminAsync(adminConnectionString, $"DROP USER \"{user}\" CASCADE");
    }

    private static async Task<bool> UserExistsAsync(string adminConnectionString, string user)
    {
        await using var connection = new OracleConnection(adminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.BindByName = true;
        command.CommandText = "SELECT COUNT(*) FROM ALL_USERS WHERE USERNAME = :user_name";
        command.Parameters.Add("user_name", OracleDbType.Varchar2, user, ParameterDirection.Input);
        return Convert.ToDecimal(
            await command.ExecuteScalarAsync(TestContext.Current.CancellationToken),
            System.Globalization.CultureInfo.InvariantCulture) == 1M;
    }

    private static async Task ExecuteAdminAsync(string adminConnectionString, string sql)
    {
        await using var connection = new OracleConnection(adminConnectionString);
        await connection.OpenAsync(TestContext.Current.CancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(TestContext.Current.CancellationToken);
    }

    private abstract class DelegatingOperations(IOracleDatabaseOperations inner)
        : IOracleDatabaseOperations
    {
        protected IOracleDatabaseOperations Inner { get; } = inner;

        public virtual ValueTask<OracleTargetProbeOutcome> ProbeTargetAsync(
            OracleConnectionStringBuilder connectionString,
            string expectedUserName,
            CancellationToken cancellationToken) =>
            Inner.ProbeTargetAsync(connectionString, expectedUserName, cancellationToken);

        public async ValueTask<IOracleAdministrativeSession> OpenAdministrativeSessionAsync(
            OracleConnectionStringBuilder connectionString,
            string expectedUserName,
            CancellationToken cancellationToken) =>
            Wrap(await Inner.OpenAdministrativeSessionAsync(
                connectionString,
                expectedUserName,
                cancellationToken));

        protected abstract IOracleAdministrativeSession Wrap(IOracleAdministrativeSession session);
    }

    private abstract class DelegatingSession(IOracleAdministrativeSession inner)
        : IOracleAdministrativeSession
    {
        protected IOracleAdministrativeSession Inner { get; } = inner;

        public ValueTask<OracleUserMatch> FindUserAsync(string userName, CancellationToken token) =>
            Inner.FindUserAsync(userName, token);

        public ValueTask CreateUserAsync(string userName, string password, CancellationToken token) =>
            Inner.CreateUserAsync(userName, password, token);

        public virtual ValueTask GrantCreateSessionAsync(string userName, CancellationToken token) =>
            Inner.GrantCreateSessionAsync(userName, token);

        public ValueTask DropUserAsync(string userName, CancellationToken token) =>
            Inner.DropUserAsync(userName, token);

        public ValueTask DisposeAsync() => Inner.DisposeAsync();
    }

    private sealed class PostGrantProbeGateOperations(
        IOracleDatabaseOperations inner,
        TaskCompletionSource grantCompleted,
        TaskCompletionSource releaseProbe) : DelegatingOperations(inner)
    {
        private int granted;

        public override async ValueTask<OracleTargetProbeOutcome> ProbeTargetAsync(
            OracleConnectionStringBuilder connectionString,
            string expectedUserName,
            CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref granted) != 0)
            {
                await releaseProbe.Task.WaitAsync(cancellationToken);
            }

            return await base.ProbeTargetAsync(connectionString, expectedUserName, cancellationToken);
        }

        protected override IOracleAdministrativeSession Wrap(IOracleAdministrativeSession session) =>
            new PostGrantSession(session, () =>
            {
                Volatile.Write(ref granted, 1);
                grantCompleted.TrySetResult();
            });

        private sealed class PostGrantSession(IOracleAdministrativeSession inner, Action completed)
            : DelegatingSession(inner)
        {
            public override async ValueTask GrantCreateSessionAsync(string userName, CancellationToken token)
            {
                await base.GrantCreateSessionAsync(userName, token);
                completed();
            }
        }
    }

    private sealed class BeforeGrantGateOperations(
        IOracleDatabaseOperations inner,
        TaskCompletionSource grantReached,
        TaskCompletionSource allowGrant) : DelegatingOperations(inner)
    {
        protected override IOracleAdministrativeSession Wrap(IOracleAdministrativeSession session) =>
            new BeforeGrantSession(session, grantReached, allowGrant);

        private sealed class BeforeGrantSession(
            IOracleAdministrativeSession inner,
            TaskCompletionSource grantReached,
            TaskCompletionSource allowGrant) : DelegatingSession(inner)
        {
            public override async ValueTask GrantCreateSessionAsync(string userName, CancellationToken token)
            {
                grantReached.TrySetResult();
                await allowGrant.Task.WaitAsync(token);
                await base.GrantCreateSessionAsync(userName, token);
            }
        }
    }

    private sealed class LostGrantAcknowledgementOperations(
        IOracleDatabaseOperations inner,
        TaskCompletionSource grantCommitted,
        TaskCompletionSource releaseFailure) : DelegatingOperations(inner)
    {
        protected override IOracleAdministrativeSession Wrap(IOracleAdministrativeSession session) =>
            new LostGrantSession(session, grantCommitted, releaseFailure);

        private sealed class LostGrantSession(
            IOracleAdministrativeSession inner,
            TaskCompletionSource grantCommitted,
            TaskCompletionSource releaseFailure) : DelegatingSession(inner)
        {
            public override async ValueTask GrantCreateSessionAsync(string userName, CancellationToken token)
            {
                await base.GrantCreateSessionAsync(userName, token);
                grantCommitted.TrySetResult();
                await releaseFailure.Task.WaitAsync(token);
                throw new OracleOperationException(OracleFailureKind.ConnectionFailed);
            }
        }
    }
}
