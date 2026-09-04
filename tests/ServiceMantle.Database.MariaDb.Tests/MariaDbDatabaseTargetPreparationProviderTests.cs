using System.IO;
using System.Net.Sockets;
using MySqlConnector;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MariaDb;
using Xunit;

namespace ServiceMantle.Database.MariaDb.Tests;

public sealed class MariaDbDatabaseTargetPreparationProviderTests
{
    private const string TargetConnectionString =
        "Server=localhost;Database=app;User ID=app;Password=target-secret";
    private const string AdministrativeConnectionString =
        "Server=localhost;Database=mysql;User ID=admin;Password=admin-secret";

    [Fact]
    public void Provider_identity_is_independent_from_MySql()
    {
        var provider = CreateProvider();

        Assert.Equal(WellKnownDatabaseProviderIds.MariaDb, provider.ProviderId);
        Assert.NotEqual(WellKnownDatabaseProviderIds.MySql, provider.ProviderId);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerDatabase, provider.TargetKind);
    }

    public static TheoryData<int, DatabaseTargetObservationStatus, bool?, string?> ObservationOutcomes => new()
    {
        { (int)MariaDbProbeOutcome.Success, DatabaseTargetObservationStatus.TargetConnectable, true, null },
        { (int)MariaDbProbeOutcome.ServerProductMismatch, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget },
        { (int)MariaDbProbeOutcome.TargetIdentityMismatch, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget },
        { (int)MariaDbProbeOutcome.DatabaseNotFound, DatabaseTargetObservationStatus.TargetMissing, false, null },
        { (int)MariaDbProbeOutcome.AuthenticationFailed, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed },
        { (int)MariaDbProbeOutcome.TargetAccessDenied, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { (int)MariaDbProbeOutcome.ConnectionFailed, DatabaseTargetObservationStatus.ServerUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.ServerUnreachable },
        { (int)MariaDbProbeOutcome.ValidationFailed, DatabaseTargetObservationStatus.ServerUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed },
    };

    [Theory]
    [MemberData(nameof(ObservationOutcomes))]
    public async Task ObserveAsync_maps_read_only_probe_outcome_without_invoking_creation(
        int outcomeValue,
        DatabaseTargetObservationStatus status,
        bool? targetExists,
        string? errorCode)
    {
        var observationProbe = new FakeObservationProbe((MariaDbProbeOutcome)outcomeValue);
        var creationProbe = new FakeCreationProbe();
        var provider = new MariaDbDatabaseTargetPreparationProvider(observationProbe, creationProbe);

        var result = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.Equal(status, result.Status);
        Assert.Equal(targetExists, result.TargetExists);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal(1, observationProbe.CallCount);
        Assert.Equal(0, creationProbe.CallCount);
    }

    [Fact]
    public async Task ObserveAsync_rejects_provider_mismatch_and_invalid_target_without_probing()
    {
        var probe = new FakeObservationProbe(MariaDbProbeOutcome.Success);
        var provider = new MariaDbDatabaseTargetPreparationProvider(probe, new FakeCreationProbe());
        var mySql = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.MySql,
            "8.4",
            TargetConnectionString);
        var invalid = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.MariaDb,
            "11.4",
            "Server=localhost;User ID=app;Password=secret");

        var mismatch = await provider.ObserveAsync(mySql, TestContext.Current.CancellationToken);
        var invalidResult = await provider.ObserveAsync(invalid, TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch, mismatch.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, invalidResult.ErrorCode);
        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task ObserveAsync_sanitizes_probe_exceptions_and_caller_cancellation()
    {
        const string secret = "observe-secret";
        var failureProvider = new MariaDbDatabaseTargetPreparationProvider(
            new FakeObservationProbe(
                MariaDbProbeOutcome.Success,
                _ => throw new InvalidOperationException($"Password={secret};Server=internal")),
            new FakeCreationProbe());

        var result = await failureProvider.ObserveAsync(
            CreateTarget(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, result.ErrorCode);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);

        using var source = new CancellationTokenSource();
        var cancellationProvider = new MariaDbDatabaseTargetPreparationProvider(
            new FakeObservationProbe(
                MariaDbProbeOutcome.Success,
                _ =>
                {
                    source.Cancel();
                    throw new InvalidOperationException($"User ID=admin;Password={secret}");
                }),
            new FakeCreationProbe());

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            cancellationProvider.ObserveAsync(CreateTarget(), source.Token).AsTask());

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin", exception.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public async Task PrepareAsync_rejects_non_positive_timeout(int milliseconds)
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CreateProvider().PrepareAsync(
                CreateRequest(),
                TimeSpan.FromMilliseconds(milliseconds),
                TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task PrepareAsync_rejects_unsupported_provider_and_invalid_names_without_creating()
    {
        var probe = new FakeCreationProbe();
        var provider = CreateProvider(creationProbe: probe);
        var mismatch = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.MySql,
                "8.4",
                TargetConnectionString),
            AdministrativeConnectionString);

        var mismatchResult = await provider.PrepareAsync(
            mismatch,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch, mismatchResult.ErrorCode);

        foreach (var invalidName in new[] { new string('a', 65), "bad\nname", "bad\ud800name" })
        {
            var result = await provider.PrepareAsync(
                CreateRequest(invalidName),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        }

        Assert.Equal(0, probe.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_uses_ephemeral_bounded_admin_settings_and_forwards_success()
    {
        var expected = DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created);
        var probe = new FakeCreationProbe((_, _, _) => ValueTask.FromResult(expected));
        var request = new DatabaseTargetPreparationRequest(
            CreateTarget("app`tenant"),
            AdministrativeConnectionString + ";Pooling=true;AutoEnlist=true;Connection Timeout=60;Default Command Timeout=90");

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            request,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.NotNull(probe.LastTargetConnectionString);
        Assert.Equal("", probe.LastTargetConnectionString.Database);
        Assert.Equal("app", probe.LastTargetConnectionString.UserID);
        Assert.Equal("target-secret", probe.LastTargetConnectionString.Password);
        Assert.False(probe.LastTargetConnectionString.Pooling);
        Assert.False(probe.LastTargetConnectionString.AutoEnlist);
        Assert.Equal("app`tenant", probe.LastDatabaseName);
        Assert.Equal(string.Empty, probe.LastConnectionString!.Database);
        Assert.False(probe.LastConnectionString.Pooling);
        Assert.False(probe.LastConnectionString.AutoEnlist);
        Assert.Equal(8U, probe.LastConnectionString.ConnectionTimeout);
        Assert.Equal(5U, probe.LastConnectionString.DefaultCommandTimeout);
        Assert.DoesNotContain("target-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret", request.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseTargetPreparationOutcome.Created)]
    [InlineData(DatabaseTargetPreparationOutcome.AlreadyExists)]
    public async Task PrepareAsync_preserves_success_outcomes(DatabaseTargetPreparationOutcome outcome)
    {
        var probe = new FakeCreationProbe((_, _, _) => ValueTask.FromResult(
            DatabaseTargetPreparationResult.Success(outcome)));

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(outcome, result.Outcome);
    }

    [Fact]
    public async Task PrepareAsync_forwards_server_product_mismatch_without_creating()
    {
        var probe = new FakeCreationProbe((_, _, _) => ValueTask.FromResult(
            DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget)));

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.Equal(1, probe.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_reports_timeout_without_leaking_admin_credentials()
    {
        var probe = new FakeCreationProbe(async (_, _, token) =>
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created);
        });

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromMilliseconds(20),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.Timeout, result.ErrorCode);
        Assert.DoesNotContain("admin-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PrepareAsync_gives_caller_cancellation_priority_and_sanitizes_it()
    {
        const string secret = "cancel-secret";
        using var source = new CancellationTokenSource();
        var probe = new FakeCreationProbe((_, _, _) =>
        {
            source.Cancel();
            throw new InvalidOperationException($"Server=internal;User ID=admin;Password={secret}");
        });

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            CreateProvider(creationProbe: probe).PrepareAsync(
                CreateRequest(),
                TimeSpan.FromMilliseconds(20),
                source.Token).AsTask());

        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(secret, exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("internal", exception.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin", exception.ToString(), StringComparison.Ordinal);
    }

    public static TheoryData<MySqlErrorCode, string> CreationErrorClassifications => new()
    {
        { MySqlErrorCode.DatabaseCreateExists, WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict },
        { MySqlErrorCode.DatabaseAccessDenied, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { MySqlErrorCode.SpecifiedAccessDeniedError, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { MySqlErrorCode.AccessDenied, WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed },
        { MySqlErrorCode.UnableToConnectToHost, WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed },
        { MySqlErrorCode.ParseError, WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed },
    };

    [Theory]
    [MemberData(nameof(CreationErrorClassifications))]
    public void Creation_failure_classifier_maps_stable_error_codes(
        MySqlErrorCode errorCode,
        string expected)
    {
        Assert.Equal(expected, MariaDbDatabaseCreationProbe.ClassifyFailure(errorCode));
    }

    [Theory]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    [InlineData(typeof(TimeoutException))]
    public void Creation_failure_classifier_maps_transport_exception_chain(Type type)
    {
        var inner = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            MariaDbDatabaseCreationProbe.ClassifyFailure(new InvalidOperationException("safe", inner)));
    }

    [Theory]
    [InlineData("app", true)]
    [InlineData("app`tenant", true)]
    [InlineData("app ", false)]
    [InlineData("app\0tenant", false)]
    public void Identifier_validation_and_quoting_are_explicit(string name, bool valid)
    {
        Assert.Equal(valid, MariaDbDatabaseTarget.IsValidDatabaseName(name));
        if (valid)
        {
            Assert.Equal(
                $"`{name.Replace("`", "``", StringComparison.Ordinal)}`",
                MariaDbDatabaseTarget.QuoteIdentifier(name));
        }
    }

    [Theory]
    [InlineData(true, false, 0, (int)MariaDbDatabaseCreationProbe.ExistingDatabaseMatch.Exact)]
    [InlineData(false, true, 0, (int)MariaDbDatabaseCreationProbe.ExistingDatabaseMatch.Missing)]
    [InlineData(false, true, 1, (int)MariaDbDatabaseCreationProbe.ExistingDatabaseMatch.Exact)]
    [InlineData(false, true, 2, (int)MariaDbDatabaseCreationProbe.ExistingDatabaseMatch.Exact)]
    [InlineData(false, false, 1, (int)MariaDbDatabaseCreationProbe.ExistingDatabaseMatch.Missing)]
    public void Existing_database_matching_follows_server_identifier_case_rules(
        bool exactMatch,
        bool caseFoldedMatch,
        int lowerCaseTableNames,
        int expectedValue)
    {
        Assert.Equal(
            (MariaDbDatabaseCreationProbe.ExistingDatabaseMatch)expectedValue,
            MariaDbDatabaseCreationProbe.ResolveExistingDatabaseMatch(
                exactMatch,
                caseFoldedMatch,
                lowerCaseTableNames));
    }

    [Theory]
    [InlineData("Server=first,second", true)]
    [InlineData("Server=first,second", false)]
    public async Task Unstable_routing_is_rejected_before_creation(string setting, bool administrative)
    {
        var request = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(WellKnownDatabaseProviderIds.MariaDb, "16",
                TargetConnectionString + (administrative ? "" : ";" + setting)),
            AdministrativeConnectionString + (administrative ? ";" + setting : ""));
        var probe = new FakeCreationProbe();
        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            request, TimeSpan.FromSeconds(1), TestContext.Current.CancellationToken);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        Assert.Null(probe.LastDatabaseName);
    }

    [Fact]
    public async Task Cancellation_precedes_a_returned_identity_rejection()
    {
        using var caller = new CancellationTokenSource();
        var probe = new FakeCreationProbe((_, _, _) =>
        {
            caller.Cancel();
            return ValueTask.FromResult(DatabaseTargetPreparationResult.Failure(
                WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget));
        });
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            CreateProvider(creationProbe: probe).PrepareAsync(
                CreateRequest(), TimeSpan.FromSeconds(5), caller.Token).AsTask());
        Assert.Equal(caller.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
    }

    private static MariaDbDatabaseTargetPreparationProvider CreateProvider(
        FakeObservationProbe? observationProbe = null,
        FakeCreationProbe? creationProbe = null) =>
        new(
            observationProbe ?? new FakeObservationProbe(MariaDbProbeOutcome.Success),
            creationProbe ?? new FakeCreationProbe());

    private static BootstrapDatabaseConfiguration CreateTarget(string database = "app") =>
        new(
            WellKnownDatabaseProviderIds.MariaDb,
            "11.4",
            $"Server=localhost;Database={database};User ID=app;Password=target-secret");

    private static DatabaseTargetPreparationRequest CreateRequest(string database = "app") =>
        new(CreateTarget(database), AdministrativeConnectionString);

    private sealed class FakeObservationProbe(
        MariaDbProbeOutcome outcome,
        Func<CancellationToken, MariaDbProbeOutcome>? handler = null) : IMariaDbBootstrapProbe
    {
        internal int CallCount { get; private set; }

        public ValueTask<MariaDbProbeOutcome> ProbeAsync(
            MySqlConnectionStringBuilder connectionString,
            int commandTimeoutSeconds,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(handler?.Invoke(cancellationToken) ?? outcome);
        }
    }

    private sealed class FakeCreationProbe(
        Func<string, MySqlConnectionStringBuilder, CancellationToken, ValueTask<DatabaseTargetPreparationResult>>? handler = null)
        : IMariaDbDatabaseCreationProbe
    {
        internal int CallCount { get; private set; }
        internal MySqlConnectionStringBuilder? LastTargetConnectionString { get; private set; }
        internal string? LastDatabaseName { get; private set; }
        internal MySqlConnectionStringBuilder? LastConnectionString { get; private set; }

        public ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
            string databaseName,
            MySqlConnectionStringBuilder targetConnectionString,
            MySqlConnectionStringBuilder administrativeConnectionString,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastTargetConnectionString = targetConnectionString;
            LastDatabaseName = databaseName;
            LastConnectionString = administrativeConnectionString;
            return handler?.Invoke(databaseName, administrativeConnectionString, cancellationToken) ??
                ValueTask.FromResult(DatabaseTargetPreparationResult.Success(
                    DatabaseTargetPreparationOutcome.AlreadyExists));
        }
    }
}
