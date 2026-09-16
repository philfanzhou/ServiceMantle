using System.Data.Common;
using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using ServiceMantle.AspNetCore.Health;
using ServiceMantle.Diagnostics;
using ServiceMantle.Health;
using ServiceMantle.Installation;
using ServiceMantle.ReferenceService.Database.PostgreSql;
using ServiceMantle.ReferenceService.Health.PostgreSql;
using ServiceMantle.ReferenceService.Management;
using ServiceMantle.ReferenceService.Telemetry;
using ServiceMantle.Testing;
using Testcontainers.PostgreSql;
using Xunit;

namespace ServiceMantle.ReferenceService.Tests;

/// <summary>
/// Accepts the sample's installation phase metric wiring through its real
/// <see cref="ReferenceApplication"/> path against a real PostgreSQL server: the explicit switch,
/// the single publishing point, the one-hot phase observations, the fail-to-unknown behavior, the
/// transparent decorator, and the cancellation and disposal boundaries.
/// </summary>
/// <remarks>
/// The metric values are read through a reader the test owns, attached to the meter provider the
/// sample itself registers. The class runs in the serialized telemetry collection because meter
/// listener state is process-wide.
/// </remarks>
[Collection(ReferenceTelemetryCollection.Name)]
[RealDatabaseTest(RealDatabaseProvider.PostgreSql)]
public sealed class ReferencePhaseMetricsTests : IAsyncLifetime
{
    private const string InstallationTable = "service_installations";
    private const string ServiceIdValue = "reference-service";

    // Synthetic fixture secrets, asserted to stay out of exported metric points and captured logs.
    private const string SyntheticUser = "reference_phase_owner";
    private const string SyntheticPassword = "synthetic-reference-phase-secret";
    private const string SyntheticRootKey = "synthetic-reference-management-root-key";

    private static CancellationToken Token => TestContext.Current.CancellationToken;

    private PostgreSqlContainer? container;
    private string? maintenanceConnectionString;

    public async ValueTask InitializeAsync()
    {
        if (!RealDatabaseTestEnvironment.IsRequired(RealDatabaseProvider.PostgreSql))
        {
            return;
        }

        container = new PostgreSqlBuilder(GetPostgresImage())
            .WithDatabase("reference_phase_maintenance")
            .WithUsername(SyntheticUser)
            .WithPassword(SyntheticPassword)
            .Build();
        await container.StartAsync(TestContext.Current.CancellationToken);
        maintenanceConnectionString = container.GetConnectionString();
    }

    public async ValueTask DisposeAsync()
    {
        if (container is not null)
        {
            await container.StopAsync(TestContext.Current.CancellationToken);
            await container.DisposeAsync();
        }
    }

    // --- A1 (M9): the off switch keeps the registration verbatim -------------------------

    [Theory]
    [InlineData(null)]
    [InlineData("false")]
    [InlineData("")]
    [InlineData("1")]
    [InlineData("yes")]
    public async Task An_unauthorized_switch_registers_no_publisher_and_no_decorator(string? value)
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey,
            "Host=127.0.0.1;Port=5432;Database=phase_metrics_registration;Username=phase;Password=phase",
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            "--" + ReferenceManagementOptions.RootKeySetting, SyntheticRootKey,
        };
        if (value is not null)
        {
            arguments.AddRange(["--" + ReferenceTelemetryDefaults.PhaseMetricsEnabledKey, value]);
        }

        // Registration only: no host is built, nothing connects.
        var builder = ReferenceApplication.CreateBuilder([.. arguments]);

        Assert.DoesNotContain(builder.Services, descriptor => descriptor.ServiceType == typeof(ServiceMetrics));
        var source = Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(IServiceHealthSnapshotSource));
        Assert.Equal(typeof(ReferencePostgreSqlHealthSnapshotSource), source.ImplementationType);
        Assert.DoesNotContain(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(ReferencePhaseMetricsSnapshotSource));
        await Task.CompletedTask;
    }

    // --- A2: the switch without the gate is refused before anything happens --------------

    [Fact]
    public void The_switch_without_the_postgresql_gate_is_refused_before_build()
    {
        var failure = Assert.Throws<InvalidOperationException>(() => ReferenceApplication.CreateBuilder(
        [
            "--" + ReferenceTelemetryDefaults.PhaseMetricsEnabledKey, "true",
        ]));

        // The message names exactly the two keys and nothing else.
        Assert.Contains(ReferenceTelemetryDefaults.PhaseMetricsEnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.Contains(ReferencePostgreSqlStartupOptions.EnabledKey, failure.Message, StringComparison.Ordinal);
        Assert.Equal(
            2,
            CountKeyNames(failure.Message, ReferenceTelemetryDefaults.PhaseMetricsEnabledKey) +
            CountKeyNames(failure.Message, ReferencePostgreSqlStartupOptions.EnabledKey));
    }

    // --- A3 (M1, M3, M4) and A4 (M5): the observed one-hot sequence ----------------------

    [Fact]
    public async Task The_metric_follows_the_authoritative_phase_and_fails_to_unknown()
    {
        RequireDatabase();
        var database = DatabaseName("one_hot_sequence");
        await CreateDatabaseAsync(database);
        await using var host = await StartAppAsync(database);
        using var client = host.App.GetTestClient();

        // M1: no read has happened, so the publisher still holds its initial unknown.
        host.Reader.Collect();
        AssertOneHot(host.Exporter, "unknown");

        // M3: a pending installation read through the ready endpoint publishes pending_setup.
        using (var ready = await client.GetAsync("/health/ready", Token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
        }

        host.Reader.Collect();
        AssertOneHot(host.Exporter, "pending_setup");

        // M4: completing the installation and adding its workspace publishes completed on the next
        // read, which now reports fully ready.
        await CompleteInstallationAsync(database);
        await InsertWorkspaceAsync(database);
        using (var ready = await client.GetAsync("/health/ready", Token))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        host.Reader.Collect();
        AssertOneHot(host.Exporter, "completed");

        // M5: the row disappearing is not a phase - the fixed failure publishes unknown, and the
        // caller observes the same fixed failure the unwired source would have raised.
        await ExecuteAsync(Target(database), $"""
            DELETE FROM public."{InstallationTable}" WHERE service_id = '{ServiceIdValue}'
            """);
        using (var failed = await client.GetAsync("/health/ready", Token))
        {
            Assert.Equal(HttpStatusCode.ServiceUnavailable, failed.StatusCode);
            var body = await failed.Content.ReadAsStringAsync(Token);
            Assert.Contains("health.probe_failed", body, StringComparison.Ordinal);
            Assert.DoesNotContain(SyntheticPassword, body, StringComparison.Ordinal);
        }

        host.Reader.Collect();
        AssertOneHot(host.Exporter, "unknown");
    }

    // --- A5 (M6): a caller cancellation publishes nothing ---------------------------------

    [Fact]
    public async Task A_cancelled_read_publishes_nothing_and_keeps_the_last_phase()
    {
        RequireDatabase();
        var database = DatabaseName("cancelled_read");
        await CreateDatabaseAsync(database);
        await using var host = await StartAppAsync(database);
        using var client = host.App.GetTestClient();
        await CompleteInstallationAsync(database);
        await InsertWorkspaceAsync(database);
        using (var ready = await client.GetAsync("/health/ready", Token))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        host.Reader.Collect();
        AssertOneHot(host.Exporter, "completed");

        var source = host.App.Services.GetRequiredService<IServiceHealthSnapshotSource>();
        using var abort = new CancellationTokenSource();
        await abort.CancelAsync();
        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await source.GetSnapshotAsync(abort.Token));

        Assert.Equal(abort.Token, cancelled.CancellationToken);
        host.Reader.Collect();
        AssertOneHot(host.Exporter, "completed");
    }

    // --- A6 (M8) and A7: the decorator is transparent -------------------------------------

    [Fact]
    public async Task A_disposed_publisher_never_hides_the_inner_observation()
    {
        RequireDatabase();
        var database = DatabaseName("disposed_publisher");
        await CreateDatabaseAsync(database);
        await using var host = await StartAppAsync(database);
        await CompleteInstallationAsync(database);
        await InsertWorkspaceAsync(database);
        var source = host.App.Services.GetRequiredService<IServiceHealthSnapshotSource>();
        var inner = host.App.Services.GetRequiredService<ReferencePostgreSqlHealthSnapshotSource>();

        await host.App.DisposeAsync();

        // The host has disposed the publisher - and with it, possibly, the pool the inner source
        // reads through. Whatever the inner observation now is, the decorator must surface exactly
        // that outcome, never the publisher's ObjectDisposedException.
        var direct = await ObserveAsync(inner);
        var throughDecorator = await ObserveAsync(source);
        Assert.Equal(direct.Phase, throughDecorator.Phase);
        Assert.Equal(direct.Exception?.GetType(), throughDecorator.Exception?.GetType());
        Assert.IsNotType<ObjectDisposedException>(throughDecorator.Exception);
    }

    [Fact]
    public async Task The_decorator_throws_the_same_exception_instance_as_the_inner_source()
    {
        RequireDatabase();
        var database = DatabaseName("transparent_failure");
        await CreateDatabaseAsync(database);
        await using var host = await StartAppAsync(database);
        var source = host.App.Services.GetRequiredService<IServiceHealthSnapshotSource>();
        var inner = host.App.Services.GetRequiredService<ReferencePostgreSqlHealthSnapshotSource>();
        // A row deleted after startup removes the authoritative phase: both observations fail
        // closed with the fixed unavailable exception, and the decorator must rethrow its own
        // instance of exactly that failure rather than swallow or replace it.
        await ExecuteAsync(Target(database), $"""
            DELETE FROM public."{InstallationTable}" WHERE service_id = '{ServiceIdValue}'
            """);

        var direct = await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => inner.GetSnapshotAsync(Token).AsTask());
        var throughDecorator = await Assert.ThrowsAsync<ReferencePostgreSqlHealthSnapshotUnavailableException>(
            () => source.GetSnapshotAsync(Token).AsTask());

        Assert.Equal(direct.Message, throughDecorator.Message);
        Assert.Null(throughDecorator.InnerException);
        host.Reader.Collect();
        AssertOneHot(host.Exporter, "unknown");
    }

    // --- A8 and concurrency ---------------------------------------------------------------

    [Fact]
    public async Task The_phase_point_carries_only_the_phase_tag_and_no_secret()
    {
        RequireDatabase();
        var database = DatabaseName("point_tags");
        await CreateDatabaseAsync(database);
        await using var host = await StartAppAsync(database);
        using var client = host.App.GetTestClient();
        await CompleteInstallationAsync(database);
        await InsertWorkspaceAsync(database);
        using (var ready = await client.GetAsync("/health/ready", Token))
        {
            Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
        }

        host.Reader.Collect();

        var points = host.Exporter.Points.ToArray();
        Assert.NotEmpty(points);
        Assert.All(
            points,
            point => Assert.Equal(
                ["phase"],
                point.Tags.Select(tag => tag.Key).Order(StringComparer.Ordinal).ToArray()));
        var rendered = string.Join('|', points.SelectMany(point => point.Tags.Select(tag => tag.Value?.ToString())));
        Assert.DoesNotContain(SyntheticPassword, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticRootKey, rendered, StringComparison.Ordinal);
        Assert.DoesNotContain(SyntheticUser, rendered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Concurrent_reads_keep_exactly_one_hot_point_per_collection()
    {
        RequireDatabase();
        var database = DatabaseName("concurrent_reads");
        await CreateDatabaseAsync(database);
        await using var host = await StartAppAsync(database);
        using var client = host.App.GetTestClient();

        var reads = Enumerable.Range(0, 16)
            .Select(_ => client.GetAsync("/health/ready", Token))
            .ToArray();
        await Task.WhenAll(reads);
        Array.ForEach(reads, response => response.Dispose());

        host.Reader.Collect();
        // The one-hot shape holds under concurrent observation; which phase won is not asserted.
        Assert.Single(host.Exporter.Points.Where(point => point.Value == 1).Select(point => point.Phase));
        Assert.DoesNotContain(host.Exporter.Points, point => point.Value != 0 && point.Value != 1);
    }

    private async Task<PhaseMetricsHost> StartAppAsync(string database)
    {
        var arguments = new List<string>
        {
            "--" + ReferencePostgreSqlStartupOptions.EnabledKey, "true",
            "--" + ReferencePostgreSqlStartupOptions.ConnectionStringKey, Target(database),
            "--" + ReferencePostgreSqlStartupOptions.PrepareIfMissingKey, "false",
            "--" + ReferenceManagementOptions.RootKeySetting, SyntheticRootKey,
            "--" + ReferenceTelemetryDefaults.PhaseMetricsEnabledKey, "true",
        };
        var builder = ReferenceApplication.CreateBuilder([.. arguments]);
        builder.WebHost.UseTestServer();
        var host = new PhaseMetricsHost(builder);
        try
        {
            await host.App.StartAsync(Token);
            return host;
        }
        catch (Exception)
        {
            await host.DisposeAsync();
            throw;
        }
    }

    private static int CountKeyNames(string message, string key)
    {
        var count = 0;
        for (var index = 0; (index = message.IndexOf(key, index, StringComparison.Ordinal)) >= 0; index += key.Length)
        {
            count++;
        }

        return count;
    }

    /// <summary>
    /// Captures one observation's outcome - the returned phase or the thrown exception - so the
    /// decorator and the inner source can be compared outcome for outcome.
    /// </summary>
    private static async Task<(ServiceStartupPhase? Phase, Exception? Exception)> ObserveAsync(
        IServiceHealthSnapshotSource source)
    {
        try
        {
            var snapshot = await source.GetSnapshotAsync(Token);
            return (snapshot.Phase, null);
        }
        catch (Exception exception)
        {
            return (null, exception);
        }
    }

    private static void AssertOneHot(PhasePointExporter exporter, string expectedPhase)
    {
        var latest = exporter.LatestPhaseValues();
        Assert.Equal(
            new Dictionary<string, long>
            {
                ["unknown"] = expectedPhase == "unknown" ? 1 : 0,
                ["bootstrap_configuration"] = 0,
                ["pending_setup"] = expectedPhase == "pending_setup" ? 1 : 0,
                ["completed"] = expectedPhase == "completed" ? 1 : 0,
            },
            latest);
    }

    private Task CompleteInstallationAsync(string database) =>
        ExecuteAsync(Target(database), $"""
            UPDATE public."{InstallationTable}"
            SET status = 1, completed_at_utc = now(), version = version + 1
            WHERE service_id = '{ServiceIdValue}'
            """);

    private Task InsertWorkspaceAsync(string database) =>
        ExecuteAsync(Target(database), """
            INSERT INTO public."reference_workspaces" ("Id", "DisplayName")
            VALUES (gen_random_uuid(), 'phase-metrics')
            """);

    private void RequireDatabase() =>
        RealDatabaseTestEnvironment.RequireAvailable(
            RealDatabaseProvider.PostgreSql, maintenanceConnectionString is not null);

    private async Task CreateDatabaseAsync(string database)
    {
        await ExecuteAsync(maintenanceConnectionString!, $"""CREATE DATABASE "{database}" """);
    }

    private string Target(string database) =>
        new NpgsqlConnectionStringBuilder(maintenanceConnectionString!)
        {
            Database = database,
        }.ConnectionString;

    private async Task ExecuteAsync(string connectionString, string statement)
    {
        await using var connection = new NpgsqlConnection(connectionString);
        await connection.OpenAsync(Token);
        await using var command = connection.CreateCommand();
        command.CommandText = statement;
        await command.ExecuteNonQueryAsync(Token);
    }

    private static string DatabaseName(string name) => $"reference_phase_{name}";

    private static string GetPostgresImage() =>
        Environment.GetEnvironmentVariable("SERVICEMANTLE_POSTGRES_IMAGE") ?? "postgres:15-alpine";

    /// <summary>Owns the app plus the reader the metric evidence is collected through.</summary>
    private sealed class PhaseMetricsHost : IAsyncDisposable
    {
        internal PhasePointExporter Exporter { get; } = new();

        internal BaseExportingMetricReader Reader { get; }

        internal WebApplication App { get; }

        internal PhaseMetricsHost(WebApplicationBuilder builder)
        {
            Reader = new BaseExportingMetricReader(Exporter);
            builder.Services.ConfigureOpenTelemetryMeterProvider((_, metrics) => metrics.AddReader(Reader));
            App = ReferenceApplication.Build(builder);
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                await App.DisposeAsync();
            }
            finally
            {
                Reader.Dispose();
                Exporter.Dispose();
            }
        }
    }

    /// <summary>Records the phase gauge points of every export, keeping only the latest batch.</summary>
    private sealed class PhasePointExporter : BaseExporter<Metric>
    {
        private readonly object gate = new();
        private List<(string Phase, long Value, KeyValuePair<string, object?>[] Tags)> latest = [];

        internal IEnumerable<(string Phase, long Value, KeyValuePair<string, object?>[] Tags)> Points
        {
            get
            {
                lock (gate)
                {
                    return latest.ToArray();
                }
            }
        }

        public override ExportResult Export(in Batch<Metric> batch)
        {
            var captured = new List<(string, long, KeyValuePair<string, object?>[])>();
            foreach (var metric in batch)
            {
                if (metric.Name != ServiceMetrics.InstallationPhaseName)
                {
                    continue;
                }

                foreach (var point in metric.GetMetricPoints())
                {
                    var tags = new List<KeyValuePair<string, object?>>();
                    foreach (var tag in point.Tags)
                    {
                        tags.Add(tag);
                    }

                    var phase = tags.FirstOrDefault(pair => pair.Key == "phase").Value?.ToString();
                    captured.Add((phase ?? "?", point.GetGaugeLastValueLong(), [.. tags]));
                }
            }

            lock (gate)
            {
                latest = captured;
            }

            return ExportResult.Success;
        }

        internal Dictionary<string, long> LatestPhaseValues()
        {
            lock (gate)
            {
                return latest.ToDictionary(point => point.Phase, point => point.Value);
            }
        }

        protected override void Dispose(bool disposing)
        {
        }
    }
}
