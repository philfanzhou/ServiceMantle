using Oracle.ManagedDataAccess.Client;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

public sealed class OracleDatabaseTargetPreparationProviderTests
{
    private const string TargetConnectionString =
        "Data Source=localhost/FREEPDB1;User Id=app_user;Password=Target-Secret-1";
    private const string AdministrativeConnectionString =
        "Data Source=localhost/FREEPDB1;User Id=system;Password=Admin-Secret-1";

    [Fact]
    public void Provider_exposes_only_Oracle_server_schema_capability()
    {
        var provider = new OracleDatabaseTargetPreparationProvider();

        Assert.Equal(WellKnownDatabaseProviderIds.Oracle, provider.ProviderId);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerSchema, provider.TargetKind);
    }

    public static TheoryData<int, DatabaseTargetObservationStatus, bool?, string?>
        ObservationOutcomes => new()
        {
            { (int)OracleTargetProbeOutcome.Success, DatabaseTargetObservationStatus.TargetConnectable, true, null },
            { (int)OracleTargetProbeOutcome.IdentityMismatch, DatabaseTargetObservationStatus.TargetUnreachable, true, WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget },
            { (int)OracleTargetProbeOutcome.UnsupportedTopology, DatabaseTargetObservationStatus.TargetUnreachable, true, WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget },
            { (int)OracleTargetProbeOutcome.TopologyPermissionDenied, DatabaseTargetObservationStatus.TargetUnreachable, true, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
            { (int)OracleTargetProbeOutcome.CreateSessionDenied, DatabaseTargetObservationStatus.TargetUnreachable, true, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
            { (int)OracleTargetProbeOutcome.AccountLocked, DatabaseTargetObservationStatus.TargetUnreachable, true, WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed },
            { (int)OracleTargetProbeOutcome.PasswordExpired, DatabaseTargetObservationStatus.TargetUnreachable, true, WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed },
            { (int)OracleTargetProbeOutcome.InvalidCredentials, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed },
            { (int)OracleTargetProbeOutcome.ConnectionFailed, DatabaseTargetObservationStatus.ServerUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed },
            { (int)OracleTargetProbeOutcome.ValidationFailed, DatabaseTargetObservationStatus.ServerUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed }
        };

    [Theory]
    [MemberData(nameof(ObservationOutcomes))]
    public async Task Observation_preserves_the_Oracle_existence_evidence_matrix(
        int outcomeValue,
        DatabaseTargetObservationStatus status,
        bool? targetExists,
        string? errorCode)
    {
        var outcome = (OracleTargetProbeOutcome)outcomeValue;
        var operations = new FakeOracleOperations { ProbeOutcome = outcome };

        var result = await new OracleDatabaseTargetPreparationProvider(operations).ObserveAsync(
            CreateTarget(),
            TestContext.Current.CancellationToken);

        Assert.Equal(status, result.Status);
        Assert.Equal(targetExists, result.TargetExists);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.NotEqual(DatabaseTargetObservationStatus.TargetMissing, result.Status);
    }

    [Fact]
    public async Task Observation_rejects_invalid_input_without_connecting_and_never_echoes_it()
    {
        var operations = new FakeOracleOperations();
        var provider = new OracleDatabaseTargetPreparationProvider(operations);
        var mismatch = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            TargetConnectionString);
        var invalid = CreateTarget(connectionString:
            "Data Source=private/FREEPDB1;User Id=C##ROOT;Password=hidden-secret");

        var mismatchResult = await provider.ObserveAsync(mismatch, TestContext.Current.CancellationToken);
        var invalidResult = await provider.ObserveAsync(invalid, TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch, mismatchResult.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, invalidResult.ErrorCode);
        Assert.Equal(0, operations.ProbeCount);
        Assert.DoesNotContain("hidden-secret", invalidResult.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("private", invalidResult.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task Preparation_requires_a_positive_bounded_timeout(int milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            new OracleDatabaseTargetPreparationProvider().PrepareAsync(
                CreateRequest(),
                TimeSpan.FromMilliseconds(milliseconds),
                TestContext.Current.CancellationToken).AsTask());
    }

    [Theory]
    [InlineData("Data Source=localhost/OTHER;User Id=system;Password=Admin-Secret-1")]
    [InlineData("Data Source=localhost/FREEPDB1;User Id=system;Password=Admin-Secret-1;DBA Privilege=SYSDBA")]
    [InlineData("Data Source=localhost/FREEPDB1;User Id=system")]
    public async Task Invalid_or_unequal_administrative_identity_fails_before_open(string adminConnectionString)
    {
        var operations = new FakeOracleOperations();
        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(administrativeConnectionString: adminConnectionString),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.Equal(0, operations.OpenCount);
    }

    [Theory]
    [InlineData("C##APP", "Target-Secret-1")]
    [InlineData("APP-NAME", "Target-Secret-1")]
    [InlineData("APP", "bad\"password")]
    [InlineData("APP", "abcdefghijklmnopqrstuvwxyz12345")]
    public async Task Unsupported_target_identifier_or_password_fails_before_DDL(
        string userName,
        string password)
    {
        var operations = new FakeOracleOperations();
        var target =
            $"Data Source=localhost/FREEPDB1;User Id={userName};Password={password}";

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(target),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.Equal(0, operations.OpenCount);
    }

    [Fact]
    public async Task Missing_user_is_created_granted_and_freshly_probed()
    {
        var session = new FakeAdministrativeSession(OracleUserMatch.Missing);
        var operations = new FakeOracleOperations();
        operations.EnqueueSession(session);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(DatabaseTargetPreparationOutcome.Created, result.Outcome);
        Assert.Equal(["create:APP_USER", "grant:APP_USER"], session.Actions);
        Assert.Equal(1, operations.ProbeCount);
        Assert.Equal("APP_USER", operations.LastExpectedUserName);
        Assert.False(operations.LastAdministrativeConnectionString!.Pooling);
        Assert.Equal("false", operations.LastAdministrativeConnectionString["Enlist"]?.ToString());
        Assert.Equal(8, operations.LastAdministrativeConnectionString.ConnectionTimeout);
        Assert.DoesNotContain("Target-Secret-1", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Admin-Secret-1", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Existing_user_is_never_modified_and_only_matching_credentials_are_adopted()
    {
        var adoptedSession = new FakeAdministrativeSession(OracleUserMatch.Exact);
        var adoptedOperations = new FakeOracleOperations();
        adoptedOperations.EnqueueSession(adoptedSession);
        var adopted = await new OracleDatabaseTargetPreparationProvider(adoptedOperations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, adopted.Outcome);
        Assert.Empty(adoptedSession.Actions);

        var conflictSession = new FakeAdministrativeSession(OracleUserMatch.Exact);
        var conflictOperations = new FakeOracleOperations { ProbeOutcome = OracleTargetProbeOutcome.InvalidCredentials };
        conflictOperations.EnqueueSession(conflictSession);
        var conflict = await new OracleDatabaseTargetPreparationProvider(conflictOperations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, conflict.ErrorCode);
        Assert.Empty(conflictSession.Actions);
    }

    [Fact]
    public async Task Conflicting_owner_shape_is_not_probed_modified_or_deleted()
    {
        var session = new FakeAdministrativeSession(OracleUserMatch.Conflicting);
        var operations = new FakeOracleOperations();
        operations.EnqueueSession(session);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, result.ErrorCode);
        Assert.Empty(session.Actions);
        Assert.Equal(0, operations.ProbeCount);
    }

    [Fact]
    public async Task Losing_create_race_adopts_only_a_connectable_exact_user()
    {
        var session = new FakeAdministrativeSession(
            [OracleUserMatch.Missing, OracleUserMatch.Exact])
        {
            CreateFailure = new OracleOperationException(OracleFailureKind.TargetConflict)
        };
        var operations = new FakeOracleOperations();
        operations.EnqueueSession(session);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(DatabaseTargetPreparationOutcome.AlreadyExists, result.Outcome);
        Assert.Equal(["create:APP_USER"], session.Actions);
        Assert.DoesNotContain(session.Actions, action => action.StartsWith("drop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task User_seen_in_another_creators_create_grant_window_returns_conflict_without_DDL()
    {
        var session = new FakeAdministrativeSession(OracleUserMatch.Exact);
        var operations = new FakeOracleOperations { ProbeOutcome = OracleTargetProbeOutcome.CreateSessionDenied };
        operations.EnqueueSession(session);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict, result.ErrorCode);
        Assert.Empty(session.Actions);
    }

    [Fact]
    public async Task Cancellation_after_create_acknowledgement_compensates_on_an_independent_budget()
    {
        using var callerSource = new CancellationTokenSource();
        var primary = new FakeAdministrativeSession(OracleUserMatch.Missing)
        {
            AfterCreate = callerSource.Cancel
        };
        var compensation = new FakeAdministrativeSession(
            [OracleUserMatch.Exact, OracleUserMatch.Missing]);
        var operations = new FakeOracleOperations();
        operations.EnqueueSession(primary);
        operations.EnqueueSession(compensation);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
                CreateRequest(),
                TimeSpan.FromSeconds(2),
                callerSource.Token).AsTask());

        Assert.Null(exception.InnerException);
        Assert.Equal(["create:APP_USER"], primary.Actions);
        Assert.Equal(["drop:APP_USER"], compensation.Actions);
        Assert.False(compensation.LastDropToken.IsCancellationRequested);
    }

    [Fact]
    public async Task Failed_compensation_outranks_caller_cancellation()
    {
        using var callerSource = new CancellationTokenSource();
        var primary = new FakeAdministrativeSession(OracleUserMatch.Missing)
        {
            AfterCreate = callerSource.Cancel
        };
        var compensation = new FakeAdministrativeSession(OracleUserMatch.Exact)
        {
            DropFailure = new OracleOperationException(OracleFailureKind.PermissionDenied)
        };
        var operations = new FakeOracleOperations();
        operations.EnqueueSession(primary);
        operations.EnqueueSession(compensation);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(2),
            callerSource.Token);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, result.ErrorCode);
        Assert.Equal(["drop:APP_USER"], compensation.Actions);
    }

    [Fact]
    public async Task Indeterminate_create_acknowledgement_never_compensates()
    {
        var primary = new FakeAdministrativeSession(OracleUserMatch.Missing)
        {
            CreateFailure = new OracleOperationException(OracleFailureKind.ConnectionFailed)
        };
        var operations = new FakeOracleOperations();
        operations.EnqueueSession(primary);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed, result.ErrorCode);
        Assert.Equal(1, operations.OpenCount);
        Assert.DoesNotContain(primary.Actions, action => action.StartsWith("drop:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Grant_is_the_irreversible_boundary_even_when_its_acknowledgement_is_lost()
    {
        var primary = new FakeAdministrativeSession(OracleUserMatch.Missing)
        {
            GrantFailure = new OracleOperationException(OracleFailureKind.ConnectionFailed)
        };
        var operations = new FakeOracleOperations();
        operations.EnqueueSession(primary);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed, result.ErrorCode);
        Assert.Equal(["create:APP_USER", "grant:APP_USER"], primary.Actions);
        Assert.Equal(1, operations.OpenCount);
    }

    [Fact]
    public async Task Cancellation_after_grant_does_not_delete_a_user_another_actor_can_adopt()
    {
        using var callerSource = new CancellationTokenSource();
        var primary = new FakeAdministrativeSession(OracleUserMatch.Missing)
        {
            AfterGrant = callerSource.Cancel
        };
        var operations = new FakeOracleOperations
        {
            ProbeHandler = (_, _, token) =>
            {
                token.ThrowIfCancellationRequested();
                return ValueTask.FromResult(OracleTargetProbeOutcome.Success);
            }
        };
        operations.EnqueueSession(primary);

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
                CreateRequest(),
                TimeSpan.FromSeconds(2),
                callerSource.Token).AsTask());

        Assert.Equal(["create:APP_USER", "grant:APP_USER"], primary.Actions);
        Assert.Equal(1, operations.OpenCount);
    }

    [Fact]
    public async Task Overall_timeout_maps_to_timeout_without_compensation_after_grant()
    {
        var primary = new FakeAdministrativeSession(OracleUserMatch.Missing);
        var operations = new FakeOracleOperations
        {
            ProbeHandler = async (_, _, token) =>
            {
                await Task.Delay(Timeout.InfiniteTimeSpan, token);
                return OracleTargetProbeOutcome.Success;
            }
        };
        operations.EnqueueSession(primary);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromMilliseconds(25),
            CancellationToken.None);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, result.ErrorCode);
        Assert.Equal(["create:APP_USER", "grant:APP_USER"], primary.Actions);
        Assert.Equal(1, operations.OpenCount);
    }

    [Theory]
    [InlineData((int)OracleFailureKind.AuthenticationFailed, WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed)]
    [InlineData((int)OracleFailureKind.PermissionDenied, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied)]
    [InlineData((int)OracleFailureKind.ConnectionFailed, WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed)]
    [InlineData((int)OracleFailureKind.InvalidTarget, WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget)]
    [InlineData((int)OracleFailureKind.Unexpected, WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed)]
    public async Task Administrative_failures_use_only_well_known_codes(
        int failureValue,
        string expectedCode)
    {
        var failure = (OracleFailureKind)failureValue;
        var operations = new ThrowingOpenOperations(failure);

        var result = await new OracleDatabaseTargetPreparationProvider(operations).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(expectedCode, result.ErrorCode);
        Assert.DoesNotContain("Admin-Secret-1", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("app", true, "APP")]
    [InlineData("App_1$", true, "APP_1$")]
    [InlineData("C##APP", false, "")]
    [InlineData("APP-NAME", false, "")]
    [InlineData("1APP", false, "")]
    public void User_name_contract_is_ASCII_unquoted_and_case_normalized(
        string candidate,
        bool valid,
        string expected)
    {
        Assert.Equal(valid, OracleDatabaseTarget.TryNormalizeUserName(candidate, out var normalized));
        Assert.Equal(expected, normalized);
    }

    [Theory]
    [InlineData(3113)]
    [InlineData(12154)]
    [InlineData(12514)]
    [InlineData(12541)]
    public void Stable_transport_error_numbers_are_classified_as_connection_failures(int number) =>
        Assert.True(OracleFailureClassifier.IsConnectionFailure(number));

    private static BootstrapDatabaseConfiguration CreateTarget(
        string connectionString = TargetConnectionString) =>
        new(WellKnownDatabaseProviderIds.Oracle, "23.26.1.0", connectionString);

    private static DatabaseTargetPreparationRequest CreateRequest(
        string targetConnectionString = TargetConnectionString,
        string administrativeConnectionString = AdministrativeConnectionString) =>
        new(CreateTarget(targetConnectionString), administrativeConnectionString);

    private sealed class ThrowingOpenOperations(OracleFailureKind failure) : IOracleDatabaseOperations
    {
        public ValueTask<OracleTargetProbeOutcome> ProbeTargetAsync(
            OracleConnectionStringBuilder connectionString,
            string expectedUserName,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(OracleTargetProbeOutcome.Success);

        public ValueTask<IOracleAdministrativeSession> OpenAdministrativeSessionAsync(
            OracleConnectionStringBuilder connectionString,
            string expectedUserName,
            CancellationToken cancellationToken) =>
            ValueTask.FromException<IOracleAdministrativeSession>(new OracleOperationException(failure));
    }
}

internal sealed class FakeAdministrativeSession : IOracleAdministrativeSession
{
    private readonly Queue<OracleUserMatch> matches;

    internal FakeAdministrativeSession(OracleUserMatch match)
        : this([match])
    {
    }

    internal FakeAdministrativeSession(IEnumerable<OracleUserMatch> matches)
    {
        this.matches = new Queue<OracleUserMatch>(matches);
    }

    internal List<string> Actions { get; } = [];
    internal Exception? CreateFailure { get; set; }
    internal Exception? GrantFailure { get; set; }
    internal Exception? DropFailure { get; set; }
    internal Action? AfterCreate { get; set; }
    internal Action? AfterGrant { get; set; }
    internal CancellationToken LastDropToken { get; private set; }

    public ValueTask<OracleUserMatch> FindUserAsync(
        string userName,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(matches.Dequeue());
    }

    public ValueTask CreateUserAsync(
        string userName,
        string password,
        CancellationToken cancellationToken)
    {
        Actions.Add($"create:{userName}");
        cancellationToken.ThrowIfCancellationRequested();
        if (CreateFailure is not null)
        {
            return ValueTask.FromException(CreateFailure);
        }

        AfterCreate?.Invoke();
        return ValueTask.CompletedTask;
    }

    public ValueTask GrantCreateSessionAsync(string userName, CancellationToken cancellationToken)
    {
        Actions.Add($"grant:{userName}");
        cancellationToken.ThrowIfCancellationRequested();
        if (GrantFailure is not null)
        {
            return ValueTask.FromException(GrantFailure);
        }

        AfterGrant?.Invoke();
        return ValueTask.CompletedTask;
    }

    public ValueTask DropUserAsync(string userName, CancellationToken cancellationToken)
    {
        Actions.Add($"drop:{userName}");
        LastDropToken = cancellationToken;
        cancellationToken.ThrowIfCancellationRequested();
        return DropFailure is null ? ValueTask.CompletedTask : ValueTask.FromException(DropFailure);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
