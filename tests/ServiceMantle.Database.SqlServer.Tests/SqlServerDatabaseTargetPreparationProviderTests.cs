using System.IO;
using System.Net.Sockets;
using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.SqlServer;
using Xunit;

namespace ServiceMantle.Database.SqlServer.Tests;

public sealed class SqlServerDatabaseTargetPreparationProviderTests
{
    private const string TargetConnectionString =
        "Server=localhost;Initial Catalog=app;User ID=app;Password=target-secret;TrustServerCertificate=true";
    private const string AdministrativeConnectionString =
        "Server=localhost;Initial Catalog=admin;User ID=admin;Password=admin-secret;TrustServerCertificate=true";

    [Fact]
    public void Provider_identity_is_independent()
    {
        var provider = CreateProvider();

        Assert.Equal(WellKnownDatabaseProviderIds.SqlServer, provider.ProviderId);
        Assert.NotEqual(WellKnownDatabaseProviderIds.PostgreSql, provider.ProviderId);
        Assert.Equal(BootstrapDatabaseTargetKind.ServerDatabase, provider.TargetKind);
    }

    public static TheoryData<int, DatabaseTargetObservationStatus, bool?, string?> ObservationOutcomes => new()
    {
        { (int)SqlServerObservationOutcome.Success, DatabaseTargetObservationStatus.TargetConnectable, true, null },
        { (int)SqlServerObservationOutcome.TargetIdentityMismatch, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget },
        { (int)SqlServerObservationOutcome.ServerVersionUnsupported, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed },
        { (int)SqlServerObservationOutcome.TargetMissing, DatabaseTargetObservationStatus.TargetMissing, false, null },
        { (int)SqlServerObservationOutcome.TargetAccessDeniedUnknown, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { (int)SqlServerObservationOutcome.TargetAccessDeniedExisting, DatabaseTargetObservationStatus.TargetUnreachable, true, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { (int)SqlServerObservationOutcome.TargetUnavailableExisting, DatabaseTargetObservationStatus.TargetUnreachable, true, WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed },
        { (int)SqlServerObservationOutcome.AuthenticationFailed, DatabaseTargetObservationStatus.TargetUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed },
        { (int)SqlServerObservationOutcome.ConnectionFailed, DatabaseTargetObservationStatus.ServerUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.ServerUnreachable },
        { (int)SqlServerObservationOutcome.ValidationFailed, DatabaseTargetObservationStatus.ServerUnreachable, null, WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed },
    };

    [Theory]
    [MemberData(nameof(ObservationOutcomes))]
    public async Task ObserveAsync_maps_read_only_probe_without_invoking_creation(
        int outcomeValue,
        DatabaseTargetObservationStatus status,
        bool? targetExists,
        string? errorCode)
    {
        var observationProbe = new FakeObservationProbe((SqlServerObservationOutcome)outcomeValue);
        var creationProbe = new FakeCreationProbe();
        var provider = new SqlServerDatabaseTargetPreparationProvider(observationProbe, creationProbe);

        var result = await provider.ObserveAsync(CreateTarget(), TestContext.Current.CancellationToken);

        Assert.Equal(status, result.Status);
        Assert.Equal(targetExists, result.TargetExists);
        Assert.Equal(errorCode, result.ErrorCode);
        Assert.Equal(1, observationProbe.CallCount);
        Assert.Equal(0, creationProbe.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_rejects_auto_attach_target_and_admin_before_any_probe()
    {
        var creationProbe = new FakeCreationProbe();
        var provider = CreateProvider(creationProbe: creationProbe);
        var autoAttachTarget = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.SqlServer,
                "16",
                TargetConnectionString + ";AttachDBFilename=/tmp/target.mdf"),
            AdministrativeConnectionString);
        var autoAttachAdmin = new DatabaseTargetPreparationRequest(
            CreateTarget(),
            AdministrativeConnectionString + ";AttachDBFilename=/tmp/admin.mdf");

        var targetResult = await provider.PrepareAsync(
            autoAttachTarget,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);
        var adminResult = await provider.PrepareAsync(
            autoAttachAdmin,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, targetResult.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, adminResult.ErrorCode);
        Assert.Equal(0, creationProbe.CallCount);
    }

    [Fact]
    public async Task ObserveAsync_rejects_provider_mismatch_and_invalid_target_without_probing()
    {
        var observationProbe = new FakeObservationProbe(SqlServerObservationOutcome.Success);
        var provider = new SqlServerDatabaseTargetPreparationProvider(
            observationProbe,
            new FakeCreationProbe());
        var mismatch = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.PostgreSql,
            "16",
            TargetConnectionString);
        var invalid = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.SqlServer,
            "16",
            "Server=localhost;User ID=app;Password=secret");
        var autoAttach = new BootstrapDatabaseConfiguration(
            WellKnownDatabaseProviderIds.SqlServer,
            "16",
            TargetConnectionString + ";AttachDBFilename=/tmp/observe.mdf");

        var mismatchResult = await provider.ObserveAsync(mismatch, TestContext.Current.CancellationToken);
        var invalidResult = await provider.ObserveAsync(invalid, TestContext.Current.CancellationToken);
        var autoAttachResult = await provider.ObserveAsync(autoAttach, TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch, mismatchResult.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, invalidResult.ErrorCode);
        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, autoAttachResult.ErrorCode);
        Assert.Equal(0, observationProbe.CallCount);
    }

    [Fact]
    public async Task ObserveAsync_sanitizes_probe_exceptions_and_caller_cancellation()
    {
        const string secret = "observe-secret";
        var failureProvider = new SqlServerDatabaseTargetPreparationProvider(
            new FakeObservationProbe(
                SqlServerObservationOutcome.Success,
                _ => throw new InvalidOperationException($"Password={secret};Server=internal")),
            new FakeCreationProbe());

        var result = await failureProvider.ObserveAsync(
            CreateTarget(),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed, result.ErrorCode);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);

        using var source = new CancellationTokenSource();
        var cancellationProvider = new SqlServerDatabaseTargetPreparationProvider(
            new FakeObservationProbe(
                SqlServerObservationOutcome.Success,
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
    public async Task PrepareAsync_rejects_provider_mismatch_and_invalid_names_without_creating()
    {
        var creationProbe = new FakeCreationProbe();
        var provider = CreateProvider(creationProbe: creationProbe);
        var mismatch = new DatabaseTargetPreparationRequest(
            new BootstrapDatabaseConfiguration(
                WellKnownDatabaseProviderIds.PostgreSql,
                "16",
                TargetConnectionString),
            AdministrativeConnectionString);

        var mismatchResult = await provider.PrepareAsync(
            mismatch,
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.ProviderMismatch, mismatchResult.ErrorCode);

        foreach (var invalidName in new[] { new string('a', 124), "bad\nname", "bad\ud800name" })
        {
            var result = await provider.PrepareAsync(
                CreateRequest(invalidName),
                TimeSpan.FromSeconds(1),
                TestContext.Current.CancellationToken);
            Assert.Equal(WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget, result.ErrorCode);
        }

        Assert.Equal(0, creationProbe.CallCount);
    }

    [Fact]
    public async Task PrepareAsync_uses_ephemeral_bounded_master_settings_and_forwards_success()
    {
        var expected = DatabaseTargetPreparationResult.Success(
            DatabaseTargetPreparationOutcome.Created);
        var probe = new FakeCreationProbe((_, _, _) => ValueTask.FromResult(expected));
        var provider = CreateProvider(creationProbe: probe);
        var request = new DatabaseTargetPreparationRequest(
            CreateTarget("app]tenant"),
            AdministrativeConnectionString +
            ";Pooling=true;Enlist=true;Connect Timeout=60;Command Timeout=90;Connect Retry Count=5");

        var result = await provider.PrepareAsync(
            request,
            TimeSpan.FromSeconds(2),
            TestContext.Current.CancellationToken);

        Assert.Same(expected, result);
        Assert.Equal("app]tenant", probe.LastDatabaseName);
        Assert.Equal("master", probe.LastConnectionString!.InitialCatalog);
        Assert.False(probe.LastConnectionString.Pooling);
        Assert.False(probe.LastConnectionString.Enlist);
        Assert.Equal(8, probe.LastConnectionString.ConnectTimeout);
        Assert.Equal(5, probe.LastConnectionString.CommandTimeout);
        Assert.Equal(0, probe.LastConnectionString.ConnectRetryCount);
        Assert.DoesNotContain("target-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("admin-secret", request.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(DatabaseTargetPreparationOutcome.Created)]
    [InlineData(DatabaseTargetPreparationOutcome.AlreadyExists)]
    public async Task PrepareAsync_preserves_success_outcomes_without_a_second_operation(
        DatabaseTargetPreparationOutcome outcome)
    {
        var probe = new FakeCreationProbe((_, _, _) => ValueTask.FromResult(
            DatabaseTargetPreparationResult.Success(outcome)));

        var result = await CreateProvider(creationProbe: probe).PrepareAsync(
            CreateRequest(),
            TimeSpan.FromSeconds(1),
            TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(outcome, result.Outcome);
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

    public static TheoryData<int, string> CreationErrorClassifications => new()
    {
        { 1801, WellKnownDatabaseTargetPreparationErrorCodes.TargetConflict },
        { 229, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { 262, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { 916, WellKnownDatabaseTargetPreparationErrorCodes.PermissionDenied },
        { 18456, WellKnownDatabaseTargetPreparationErrorCodes.AuthenticationFailed },
        { 53, WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed },
        { -2, WellKnownDatabaseTargetPreparationErrorCodes.Timeout },
        { 102, WellKnownDatabaseTargetPreparationErrorCodes.PreparationFailed },
    };

    [Theory]
    [MemberData(nameof(CreationErrorClassifications))]
    public void Creation_failure_classifier_maps_stable_error_codes(
        int errorNumber,
        string expected)
    {
        Assert.Equal(expected, SqlServerDatabaseCreationProbe.ClassifyFailure(errorNumber));
    }

    [Theory]
    [InlineData(typeof(SocketException))]
    [InlineData(typeof(IOException))]
    public void Creation_failure_classifier_maps_transport_exception_chains(Type type)
    {
        var inner = (Exception)Activator.CreateInstance(type)!;

        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.ConnectionFailed,
            SqlServerDatabaseCreationProbe.ClassifyFailure(new InvalidOperationException("safe", inner)));
    }

    [Fact]
    public void Creation_failure_classifier_maps_runtime_timeout_to_timeout()
    {
        Assert.Equal(
            WellKnownDatabaseTargetPreparationErrorCodes.Timeout,
            SqlServerDatabaseCreationProbe.ClassifyFailure(new TimeoutException("safe")));
    }

    public static TheoryData<int?, int?, int?, int> ExistingDatabaseStates => new()
    {
        { 0, 0, 1, (int)SqlServerDatabaseCreationProbe.ExistingDatabaseMatch.Exact },
        { 0, 0, 2, (int)SqlServerDatabaseCreationProbe.ExistingDatabaseMatch.Conflicting },
        { 1, 0, null, (int)SqlServerDatabaseCreationProbe.ExistingDatabaseMatch.Missing },
        { 0, 1, null, (int)SqlServerDatabaseCreationProbe.ExistingDatabaseMatch.Missing },
        { 0, 0, null, (int)SqlServerDatabaseCreationProbe.ExistingDatabaseMatch.VisibilityUnknown },
        { null, null, null, (int)SqlServerDatabaseCreationProbe.ExistingDatabaseMatch.VisibilityUnknown },
    };

    [Theory]
    [MemberData(nameof(ExistingDatabaseStates))]
    public void Creation_requires_complete_visibility_before_treating_no_row_as_missing(
        int? hasViewAnyDatabase,
        int? hasAlterAnyDatabase,
        int? match,
        int expectedValue)
    {
        Assert.Equal(
            (SqlServerDatabaseCreationProbe.ExistingDatabaseMatch)expectedValue,
            SqlServerDatabaseCreationProbe.InterpretExistingDatabase(
                hasViewAnyDatabase,
                hasAlterAnyDatabase,
                match));
    }

    [Theory]
    [InlineData("app", true)]
    [InlineData("app]tenant", true)]
    [InlineData("app ", false)]
    [InlineData("app\0tenant", false)]
    public void Identifier_validation_and_quoting_are_explicit(string name, bool valid)
    {
        Assert.Equal(valid, SqlServerDatabaseTarget.IsValidDatabaseName(name));
        if (valid)
        {
            Assert.Equal(
                $"[{name.Replace("]", "]]", StringComparison.Ordinal)}]",
                SqlServerDatabaseTarget.QuoteIdentifier(name));
        }
    }

    [Fact]
    public void Database_name_limit_accounts_for_generated_logical_log_file_name()
    {
        Assert.True(SqlServerDatabaseTarget.IsValidDatabaseName(new string('a', 123)));
        Assert.False(SqlServerDatabaseTarget.IsValidDatabaseName(new string('a', 124)));
        Assert.Equal("Latin1_General_100_CI_AS_SC_UTF8", SqlServerDatabaseCreationProbe.DatabaseCollation);
    }

    private static SqlServerDatabaseTargetPreparationProvider CreateProvider(
        FakeObservationProbe? observationProbe = null,
        FakeCreationProbe? creationProbe = null) =>
        new(
            observationProbe ?? new FakeObservationProbe(SqlServerObservationOutcome.Success),
            creationProbe ?? new FakeCreationProbe());

    private static BootstrapDatabaseConfiguration CreateTarget(string database = "app") =>
        new(
            WellKnownDatabaseProviderIds.SqlServer,
            "16.0",
            $"Server=localhost;Initial Catalog={database};User ID=app;Password=target-secret;TrustServerCertificate=true");

    private static DatabaseTargetPreparationRequest CreateRequest(string database = "app") =>
        new(CreateTarget(database), AdministrativeConnectionString);

    private sealed class FakeObservationProbe(
        SqlServerObservationOutcome outcome,
        Func<CancellationToken, SqlServerObservationOutcome>? handler = null)
        : ISqlServerTargetObservationProbe
    {
        internal int CallCount { get; private set; }

        public ValueTask<SqlServerObservationOutcome> ObserveAsync(
            SqlConnectionStringBuilder connectionString,
            CancellationToken cancellationToken)
        {
            CallCount++;
            return ValueTask.FromResult(handler?.Invoke(cancellationToken) ?? outcome);
        }
    }

    private sealed class FakeCreationProbe(
        Func<string, SqlConnectionStringBuilder, CancellationToken, ValueTask<DatabaseTargetPreparationResult>>? handler = null)
        : ISqlServerDatabaseCreationProbe
    {
        internal int CallCount { get; private set; }
        internal string? LastDatabaseName { get; private set; }
        internal SqlConnectionStringBuilder? LastConnectionString { get; private set; }

        public ValueTask<DatabaseTargetPreparationResult> CreateIfMissingAsync(
            string databaseName,
            SqlConnectionStringBuilder administrativeConnectionString,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastDatabaseName = databaseName;
            LastConnectionString = administrativeConnectionString;
            return handler?.Invoke(databaseName, administrativeConnectionString, cancellationToken) ??
                ValueTask.FromResult(DatabaseTargetPreparationResult.Success(
                    DatabaseTargetPreparationOutcome.AlreadyExists));
        }
    }
}
