using System.Data;
using System.Data.Common;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Persistence.Relational.Tests;

/// <summary>
/// Unit tests for <see cref="EfCoreHealthSnapshotSource{TDbContext}"/>: receipt gating, the two
/// probe modes, the fixed failure mapping through the classification seam, cancellation
/// propagation, and error-code prefix validation. SQLite backs the EF Core model; provider
/// classification is stubbed per test.
/// </summary>
public sealed class EfCoreHealthSnapshotSourceTests
{
    private const string Prefix = "catalog";

    [Fact]
    public async Task Receipt_not_started_reports_pending_setup_without_touching_the_database()
    {
        // The connection string points at a directory that cannot exist: if the source tried to
        // open it, the call would fail, proving the receipt gate happens first.
        var receipt = new StartupDatabaseReceipt();
        await using var context = CreateContext(BrokenConnectionString());
        var source = CreateSource(receipt, context, StubClassifier.ConnectionFailure);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.PendingSetup, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.NotStarted, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Unreachable, snapshot.DatabaseStatus);
        Assert.Equal("catalog.startup_incomplete", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Receipt_running_reports_incomplete_startup_without_touching_the_database()
    {
        var receipt = new StartupDatabaseReceipt();
        Assert.True(receipt.TryMarkRunning());
        await using var context = CreateContext(BrokenConnectionString());
        var source = CreateSource(receipt, context, StubClassifier.ConnectionFailure);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.PendingSetup, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Running, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Unreachable, snapshot.DatabaseStatus);
        Assert.Equal("catalog.startup_incomplete", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Receipt_failed_reports_startup_failure_without_touching_the_database()
    {
        var receipt = new StartupDatabaseReceipt();
        Assert.True(receipt.TryMarkRunning());
        Assert.True(receipt.TryCompleteFailed(WellKnownMigrationErrorCodes.ExecutionFailed));
        await using var context = CreateContext(BrokenConnectionString());
        var source = CreateSource(receipt, context, StubClassifier.ConnectionFailure);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.PendingSetup, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Failed, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Unreachable, snapshot.DatabaseStatus);
        Assert.Equal("catalog.startup_failed", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Receipt_gating_performs_zero_database_operations()
    {
        var receipt = new StartupDatabaseReceipt();
        Assert.True(receipt.TryMarkRunning());
        var connectionRecorder = new ConnectionRecorder();
        var databaseFile = CreateDatabaseFile();
        await using var connection = new ProbeDbConnection(
            new SqliteConnection(DatabaseConnectionString(databaseFile)));
        await using var context = CreateContext(connection, connectionRecorder);
        var source = CreateSource(receipt, context, StubClassifier.Unclassified);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceMigrationReadinessState.Running, snapshot.MigrationStatus);
        Assert.Equal(0, connectionRecorder.Openings);
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task Mapped_schema_probe_reports_reachable_when_the_model_matches()
    {
        var connectionRecorder = new ConnectionRecorder();
        var databaseFile = CreateDatabaseFile();
        await using (var setup = CreateContext(DatabaseConnectionString(databaseFile)))
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        // The wrapped connection observes the raw ADO commands the probe issues (EF command
        // interceptors do not see them, because the probe deliberately bypasses EF's command
        // pipeline).
        await using var connection = new ProbeDbConnection(
            new SqliteConnection(DatabaseConnectionString(databaseFile)));
        await using var context = CreateContext(connection, connectionRecorder);
        var source = CreateSource(
            SucceededReceipt(),
            context,
            StubClassifier.ConnectionFailure,
            EfCoreHealthSnapshotProbeMode.MappedSchema);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
        Assert.Null(snapshot.ErrorCode);
        Assert.Equal(1, connectionRecorder.Openings);

        // One zero-row SELECT per mapped table, quoting keyword-shaped identifiers, and no row
        // data: every probe ends in the universal zero-row predicate.
        Assert.Equal(2, connection.Commands.Count);
        Assert.All(connection.Commands, text =>
        {
            Assert.EndsWith("WHERE 1 = 0", text, StringComparison.Ordinal);
        });
        Assert.Contains(connection.Commands, text =>
            text.Contains("probe_samples", StringComparison.Ordinal));
        Assert.Contains(connection.Commands, text =>
            text.Contains("delimited_group", StringComparison.Ordinal) &&
            text.Contains("reserved_order", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Connection_only_probe_reports_reachable_without_running_any_command()
    {
        var connectionRecorder = new ConnectionRecorder();
        var databaseFile = CreateDatabaseFile();

        await using var connection = new ProbeDbConnection(
            new SqliteConnection(DatabaseConnectionString(databaseFile)));
        await using var context = CreateContext(connection, connectionRecorder);
        var source = CreateSource(
            SucceededReceipt(),
            context,
            StubClassifier.ConnectionFailure,
            EfCoreHealthSnapshotProbeMode.ConnectionOnly);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
        Assert.Null(snapshot.ErrorCode);
        Assert.Equal(1, connectionRecorder.Openings);
        Assert.Empty(connection.Commands);
    }

    [Fact]
    public async Task Classified_connection_failure_reports_unreachable_with_migration_untouched()
    {
        await using var context = CreateContext(BrokenConnectionString());
        var source = CreateSource(SucceededReceipt(), context, StubClassifier.ConnectionFailure);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Succeeded, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Unreachable, snapshot.DatabaseStatus);
        Assert.Equal("catalog.database_unreachable", snapshot.ErrorCode);
    }

    [Fact]
    public async Task Classified_schema_failure_reports_failed_migration_while_reachable()
    {
        // An existing but empty database file: the connection opens, the mapped table does not
        // exist, so the zero-row probe fails with a schema-shaped provider exception.
        var databaseFile = CreateDatabaseFile();
        await using var context = CreateContext(DatabaseConnectionString(databaseFile));
        var source = CreateSource(SucceededReceipt(), context, StubClassifier.SchemaUnreadable);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.Completed, snapshot.Phase);
        Assert.Equal(ServiceMigrationReadinessState.Failed, snapshot.MigrationStatus);
        Assert.Equal(ServiceDatabaseReadinessState.Reachable, snapshot.DatabaseStatus);
        Assert.Equal("catalog.schema_unavailable", snapshot.ErrorCode);
        Assert.DoesNotContain("Sqlite", snapshot.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("no such table", snapshot.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Unclassified_failure_propagates_the_original_exception()
    {
        var databaseFile = CreateDatabaseFile();
        await using var context = CreateContext(DatabaseConnectionString(databaseFile));
        var source = CreateSource(SucceededReceipt(), context, StubClassifier.Unclassified);

        // The schema probe fails (the mapped table is absent) and the classifier declines to
        // classify: the provider exception itself reaches the caller.
        var exception = await Assert.ThrowsAsync<SqliteException>(
            () => source.GetSnapshotAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Contains("no such table", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_classifier_that_throws_fails_closed_with_its_own_exception()
    {
        var databaseFile = CreateDatabaseFile();
        await using var context = CreateContext(DatabaseConnectionString(databaseFile));
        var source = CreateSource(
            SucceededReceipt(),
            context,
            new ThrowingClassifier(new InvalidOperationException("classifier bug")));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => source.GetSnapshotAsync(TestContext.Current.CancellationToken).AsTask());
    }

    [Fact]
    public async Task Caller_cancellation_before_the_probe_propagates_on_the_callers_token()
    {
        await using var context = CreateContext(BrokenConnectionString());
        var source = CreateSource(SucceededReceipt(), context, StubClassifier.ConnectionFailure);
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetSnapshotAsync(cancelled.Token).AsTask());

        Assert.Equal(cancelled.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Driver_wrapped_cancellation_is_not_swallowed_as_unreachable()
    {
        // The provider throws its own exception while the caller's token is already cancelled —
        // the Npgsql-style wrapped cancellation. The result must be an OperationCanceledException
        // carrying the received token, never an Unreachable snapshot.
        using var cancellation = new CancellationTokenSource();
        var databaseFile = CreateDatabaseFile();
        await using (var setup = CreateContext(DatabaseConnectionString(databaseFile)))
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        await using var connection = new ProbeDbConnection(
            new SqliteConnection(DatabaseConnectionString(databaseFile)))
        {
            OnExecuteReader = () =>
            {
                cancellation.Cancel();
                return new InvalidOperationException("driver-wrapped cancellation");
            },
        };
        await using var context = CreateContext(connection);
        var source = CreateSource(SucceededReceipt(), context, StubClassifier.ConnectionFailure);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetSnapshotAsync(cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Provider_cancellation_exception_propagates_unchanged()
    {
        var providerCancellation = new OperationCanceledException("provider budget");
        var databaseFile = CreateDatabaseFile();
        await using (var setup = CreateContext(DatabaseConnectionString(databaseFile)))
        {
            await setup.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
        }

        await using var connection = new ProbeDbConnection(
            new SqliteConnection(DatabaseConnectionString(databaseFile)))
        {
            OnExecuteReader = () => providerCancellation,
        };
        await using var context = CreateContext(connection);
        var source = CreateSource(SucceededReceipt(), context, StubClassifier.ConnectionFailure);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(
            () => source.GetSnapshotAsync(TestContext.Current.CancellationToken).AsTask());

        Assert.Same(providerCancellation, exception);
    }

    [Fact]
    public async Task Every_snapshot_and_log_surface_stays_free_of_database_failure_text()
    {
        var databaseFile = CreateDatabaseFile();
        await using var context = CreateContext(DatabaseConnectionString(databaseFile));
        var source = CreateSource(SucceededReceipt(), context, StubClassifier.SchemaUnreadable);

        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);
        var rendered = snapshot.ToString();

        Assert.Equal("catalog.schema_unavailable", snapshot.ErrorCode);
        Assert.DoesNotContain("SqliteException", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("no such table", rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(databaseFile, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain("Data Source", rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Registered_source_resolves_from_the_container_and_reports_the_receipt()
    {
        var services = new ServiceCollection();
        var receipt = new StartupDatabaseReceipt();
        services.AddSingleton(receipt);
        var databaseFile = CreateDatabaseFile();
        services.AddScoped(_ => CreateContext(DatabaseConnectionString(databaseFile)));
        services.AddServiceMantleEfCoreHealthSnapshotSource<SnapshotDbContext>(
            ServiceId.Parse("registered-service"),
            StubClassifier.ConnectionFailure);

        await using var provider = services.BuildServiceProvider();
        using var scope = provider.CreateScope();
        var typedSource = scope.ServiceProvider
            .GetRequiredService<EfCoreHealthSnapshotSource<SnapshotDbContext>>();
        var source = scope.ServiceProvider.GetRequiredService<IServiceHealthSnapshotSource>();

        Assert.Same(typedSource, source);
        var snapshot = await source.GetSnapshotAsync(TestContext.Current.CancellationToken);

        Assert.Equal(ServiceStartupPhase.PendingSetup, snapshot.Phase);
        Assert.Equal("registered-service.startup_incomplete", snapshot.ErrorCode);
    }

    [Fact]
    public void Registration_rejects_an_invalid_explicit_prefix_at_registration_time()
    {
        var services = new ServiceCollection();

        Assert.Throws<ArgumentException>(() =>
            services.AddServiceMantleEfCoreHealthSnapshotSource<SnapshotDbContext>(
                ServiceId.Parse("catalog"),
                StubClassifier.ConnectionFailure,
                errorCodePrefix: "invalid prefix with spaces"));
        Assert.Throws<ArgumentException>(() =>
            services.AddServiceMantleEfCoreHealthSnapshotSource<SnapshotDbContext>(
                ServiceId.Parse("catalog"),
                StubClassifier.ConnectionFailure,
                errorCodePrefix: ".starts-with-a-dot"));
        Assert.Throws<ArgumentException>(() =>
            services.AddServiceMantleEfCoreHealthSnapshotSource<SnapshotDbContext>(
                ServiceId.Parse("catalog"),
                StubClassifier.ConnectionFailure,
                errorCodePrefix: new string('a', EfCoreHealthSnapshotSource<SnapshotDbContext>.MaximumErrorCodePrefixLength + 1)));
    }

    [Fact]
    public void Registration_rejects_a_service_identifier_too_long_to_derive_a_prefix()
    {
        var services = new ServiceCollection();
        var longIdentifier = new string('a', 115);

        var exception = Assert.Throws<ArgumentException>(() =>
            services.AddServiceMantleEfCoreHealthSnapshotSource<SnapshotDbContext>(
                ServiceId.Parse(longIdentifier),
                StubClassifier.ConnectionFailure));

        Assert.Equal("serviceId", exception.ParamName);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(".leading-dot")]
    [InlineData("contains space")]
    [InlineData("über")]
    public void Constructor_rejects_unsafe_error_code_prefixes(string prefix)
    {
        var receipt = new StartupDatabaseReceipt();
        using var context = CreateContext(DatabaseConnectionString(CreateDatabaseFile()));

        Assert.Throws<ArgumentException>(() =>
            CreateSource(receipt, context, StubClassifier.ConnectionFailure, errorCodePrefix: prefix));
    }

    [Fact]
    public void Constructor_rejects_null_arguments_and_undefined_probe_modes()
    {
        var receipt = new StartupDatabaseReceipt();
        using var context = CreateContext(DatabaseConnectionString(CreateDatabaseFile()));

        Assert.Throws<ArgumentNullException>(() => new EfCoreHealthSnapshotSource<SnapshotDbContext>(
            null!, context, StubClassifier.ConnectionFailure, Prefix));
        Assert.Throws<ArgumentNullException>(() => new EfCoreHealthSnapshotSource<SnapshotDbContext>(
            receipt, null!, StubClassifier.ConnectionFailure, Prefix));
        Assert.Throws<ArgumentNullException>(() => new EfCoreHealthSnapshotSource<SnapshotDbContext>(
            receipt, context, null!, Prefix));
        Assert.Throws<ArgumentOutOfRangeException>(() => new EfCoreHealthSnapshotSource<SnapshotDbContext>(
            receipt, context, StubClassifier.ConnectionFailure, Prefix, (EfCoreHealthSnapshotProbeMode)99));
    }

    [Fact]
    public void Default_prefix_derivation_accepts_every_valid_service_identifier()
    {
        // A service identifier within the prefix bound derives its prefix without changes.
        var services = new ServiceCollection();
        services.AddServiceMantleEfCoreHealthSnapshotSource<SnapshotDbContext>(
            ServiceId.Parse("a.sane-length-identifier"),
            StubClassifier.ConnectionFailure);

        var descriptor = Assert.Single(
            services.Where(service =>
                service.ServiceType == typeof(IServiceHealthSnapshotSource)));
        Assert.Equal(ServiceLifetime.Scoped, descriptor.Lifetime);
    }

    private static StartupDatabaseReceipt SucceededReceipt()
    {
        var receipt = new StartupDatabaseReceipt();
        Assert.True(receipt.TryMarkRunning());
        Assert.True(receipt.TryCompleteSucceeded());
        return receipt;
    }

    private static EfCoreHealthSnapshotSource<SnapshotDbContext> CreateSource(
        StartupDatabaseReceipt receipt,
        SnapshotDbContext context,
        IServiceDatabaseProbeFailureClassifier classifier,
        EfCoreHealthSnapshotProbeMode probeMode = EfCoreHealthSnapshotProbeMode.MappedSchema,
        string errorCodePrefix = Prefix) =>
        new(receipt, context, classifier, errorCodePrefix, probeMode);

    private static string DatabaseConnectionString(string databaseFile) =>
        $"Data Source={databaseFile}";

    private static string BrokenConnectionString() =>
        "Data Source=/definitely-missing-sm-health-directory/db.sqlite?Mode=ReadOnly";

    private static string CreateDatabaseFile()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "servicemantle-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return Path.Combine(directory, "health.db");
    }

    private static SnapshotDbContext CreateContext(
        string connectionString,
        ConnectionRecorder? connectionRecorder = null)
    {
        var builder = new DbContextOptionsBuilder<SnapshotDbContext>()
            .UseSqlite(connectionString);
        if (connectionRecorder is not null)
        {
            builder.AddInterceptors(connectionRecorder);
        }

        return new SnapshotDbContext(builder.Options);
    }

    private static SnapshotDbContext CreateContext(
        DbConnection connection,
        ConnectionRecorder? connectionRecorder = null)
    {
        var builder = new DbContextOptionsBuilder<SnapshotDbContext>()
            .UseSqlite(connection);
        if (connectionRecorder is not null)
        {
            builder.AddInterceptors(connectionRecorder);
        }

        return new SnapshotDbContext(builder.Options);
    }

    private sealed class StubClassifier(Func<Exception, ServiceDatabaseProbeFailureKind> classify)
        : IServiceDatabaseProbeFailureClassifier
    {
        public static StubClassifier ConnectionFailure { get; } =
            new(_ => ServiceDatabaseProbeFailureKind.ConnectionFailure);

        public static StubClassifier SchemaUnreadable { get; } =
            new(_ => ServiceDatabaseProbeFailureKind.SchemaUnreadable);

        public static StubClassifier Unclassified { get; } =
            new(_ => ServiceDatabaseProbeFailureKind.Unclassified);

        public ServiceDatabaseProbeFailureKind Classify(Exception exception) => classify(exception);
    }

    private sealed class ThrowingClassifier(Exception exception)
        : IServiceDatabaseProbeFailureClassifier
    {
        public ServiceDatabaseProbeFailureKind Classify(Exception _) => throw exception;
    }

    private sealed class ConnectionRecorder : DbConnectionInterceptor
    {
        public int Openings { get; private set; }

        public override InterceptionResult ConnectionOpening(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result)
        {
            Openings++;
            return result;
        }

        public override ValueTask<InterceptionResult> ConnectionOpeningAsync(
            DbConnection connection,
            ConnectionEventData eventData,
            InterceptionResult result,
            CancellationToken cancellationToken = default)
        {
            Openings++;
            return ValueTask.FromResult(result);
        }
    }

    /// <summary>
    /// A connection wrapper that observes the raw ADO commands executed by the health probe and
    /// can inject one exception per reader execution. EF Core command interceptors never see
    /// these commands, because the probe deliberately bypasses EF's command pipeline; the
    /// wrapper is the single observation point.
    /// </summary>
    private sealed class ProbeDbConnection(DbConnection inner) : DbConnection
    {
        public List<string> Commands { get; } = [];

        public Func<Exception?>? OnExecuteReader { get; set; }

        public override string ConnectionString
        {
            get => inner.ConnectionString;
            set => inner.ConnectionString = value;
        }

        public override string Database => inner.Database;

        public override string DataSource => inner.DataSource;

        public override string ServerVersion => inner.ServerVersion;

        public override ConnectionState State => inner.State;

        public override void ChangeDatabase(string databaseName) => inner.ChangeDatabase(databaseName);

        public override void Close() => inner.Close();

        public override void Open() => inner.Open();

        protected override DbTransaction BeginDbTransaction(IsolationLevel isolationLevel) =>
            inner.BeginTransaction(isolationLevel);

        protected override DbCommand CreateDbCommand() => new ProbeDbCommand(this, inner.CreateCommand());

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
            }

            base.Dispose(disposing);
        }

        private sealed class ProbeDbCommand(ProbeDbConnection owner, DbCommand inner) : DbCommand
        {
            public override string CommandText
            {
                get => inner.CommandText;
                set => inner.CommandText = value;
            }

            public override int CommandTimeout
            {
                get => inner.CommandTimeout;
                set => inner.CommandTimeout = value;
            }

            public override CommandType CommandType
            {
                get => inner.CommandType;
                set => inner.CommandType = value;
            }

            public override bool DesignTimeVisible
            {
                get => inner.DesignTimeVisible;
                set => inner.DesignTimeVisible = value;
            }

            public override UpdateRowSource UpdatedRowSource
            {
                get => inner.UpdatedRowSource;
                set => inner.UpdatedRowSource = value;
            }

            protected override DbConnection DbConnection
            {
                get => inner.Connection;
                set => inner.Connection = value;
            }

            protected override DbParameterCollection DbParameterCollection => inner.Parameters;

            protected override DbTransaction? DbTransaction
            {
                get => inner.Transaction;
                set => inner.Transaction = value;
            }

            public override void Cancel() => inner.Cancel();

            public override int ExecuteNonQuery() => inner.ExecuteNonQuery();

            public override object ExecuteScalar() => inner.ExecuteScalar();

            public override void Prepare() => inner.Prepare();

            protected override DbParameter CreateDbParameter() => inner.CreateParameter();

            protected override DbDataReader ExecuteDbDataReader(CommandBehavior behavior)
            {
                var injected = owner.OnExecuteReader?.Invoke();
                if (injected is not null)
                {
                    throw injected;
                }

                owner.Commands.Add(CommandText);
                return inner.ExecuteReader(behavior);
            }

            protected override async Task<DbDataReader> ExecuteDbDataReaderAsync(
                CommandBehavior behavior,
                CancellationToken cancellationToken)
            {
                var injected = owner.OnExecuteReader?.Invoke();
                if (injected is not null)
                {
                    throw injected;
                }

                owner.Commands.Add(CommandText);
                return await inner.ExecuteReaderAsync(behavior, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
    }

    private sealed class SnapshotDbContext(DbContextOptions<SnapshotDbContext> options)
        : DbContext(options)
    {
        public DbSet<ProbeSample> Samples => Set<ProbeSample>();

        public DbSet<DelimitedSample> DelimitedSamples => Set<DelimitedSample>();

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.Entity<ProbeSample>().ToTable("probe_samples");
            modelBuilder.Entity<DelimitedSample>()
                .ToTable("delimited_group")
                .Property(sample => sample.ReservedOrder)
                .HasColumnName("reserved_order");
        }
    }

    private sealed class ProbeSample
    {
        public int Id { get; set; }

        public string Name { get; set; } = string.Empty;
    }

    private sealed class DelimitedSample
    {
        public int Id { get; set; }

        public int ReservedOrder { get; set; }
    }
}
