using System.Collections.Concurrent;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using ServiceMantle.Installation;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Tests;

public sealed class ServiceMantleMetricsTests
{
    [Fact]
    public async Task Fixed_contract_has_five_series_and_only_explicit_phase_tags()
    {
        var exporter = new CaptureExporter();
        using var host = CreateHost("catalog-01", exporter, repeat: true);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var publisher = host.Services.GetRequiredService<ServiceMantleMetrics>();
        var provider = host.Services.GetRequiredService<MeterProvider>();
        Assert.Single(host.Services.GetServices<ServiceMantleMetrics>());
        Assert.Single(host.Services.GetServices<MeterProvider>());
        Assert.Null(host.Services.GetService<global::OpenTelemetry.Trace.TracerProvider>());
        var resource = provider.GetResource().Attributes.ToDictionary(pair => pair.Key, pair => pair.Value);
        Assert.Equal(new Dictionary<string, object>
        {
            ["service.name"] = "catalog",
            ["service.version"] = "1.2.3",
            ["service.instance.id"] = "catalog-01"
        }, resource);
        AssertPhase(provider, exporter, "unknown");
        foreach (var (phase, label) in new[]
        {
            (ServiceStartupPhase.BootstrapConfiguration, "bootstrap_configuration"),
            (ServiceStartupPhase.PendingSetup, "pending_setup"),
            (ServiceStartupPhase.Completed, "completed")
        })
        {
            publisher.SetPhase(phase);
            AssertPhase(provider, exporter, label);
        }
        publisher.SetUnknown();
        AssertPhase(provider, exporter, "unknown");
        Assert.Equal(5, exporter.SeenSeries.Count);
        Assert.All(exporter.Points, point =>
        {
            Assert.Equal("1", point.Unit);
            Assert.Equal(ServiceMantleMetrics.MeterName, point.MeterName);
            Assert.Equal(ServiceMantleMetrics.MeterVersion, point.MeterVersion);
            Assert.All(point.Tags.Keys, key => Assert.Equal("phase", key));
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Metrics_compose_with_existing_instrumentation_in_both_registration_orders(bool metricsFirst)
    {
        var exporter = new CaptureExporter();
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var service = builder.Services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"), serviceVersion: "1.2.3");
        if (metricsFirst) service.AddServiceMantleMetrics();
        service.AddOpenTelemetryInstrumentation();
        if (!metricsFirst) service.AddServiceMantleMetrics();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(
            new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: int.MaxValue)));
        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.Single(host.Services.GetServices<MeterProvider>());
        AssertPhase(host.Services.GetRequiredService<MeterProvider>(), exporter, "unknown");
    }

    [Fact]
    public async Task Independent_hosts_and_forged_same_name_meters_cannot_cross_publish()
    {
        var firstExporter = new CaptureExporter();
        var secondExporter = new CaptureExporter();
        using var first = CreateHost("catalog-01", firstExporter);
        using var second = CreateHost("catalog-02", secondExporter);
        await first.StartAsync(TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken);
        using var external = new Meter(ServiceMantleMetrics.MeterName, ServiceMantleMetrics.MeterVersion);
        external.CreateObservableGauge(ServiceMantleMetrics.InstallationPhaseName, () =>
            new Measurement<long>(999, new KeyValuePair<string, object?>("phase", "Password=secret"),
                new KeyValuePair<string, object?>("user.id", Guid.NewGuid().ToString())));
        external.CreateCounter<long>("request.secret").Add(1, new KeyValuePair<string, object?>("credential", "secret"));
        first.Services.GetRequiredService<ServiceMantleMetrics>().SetPhase(ServiceStartupPhase.PendingSetup);
        second.Services.GetRequiredService<ServiceMantleMetrics>().SetPhase(ServiceStartupPhase.Completed);
        AssertPhase(first.Services.GetRequiredService<MeterProvider>(), firstExporter, "pending_setup");
        AssertPhase(second.Services.GetRequiredService<MeterProvider>(), secondExporter, "completed");
        Assert.Equal(5, firstExporter.SeenSeries.Count);
        Assert.Equal(5, secondExporter.SeenSeries.Count);
    }

    [Theory]
    [InlineData(-1)]
    [InlineData(3)]
    [InlineData(int.MaxValue)]
    public async Task Invalid_phase_does_not_change_last_observation(int invalid)
    {
        var exporter = new CaptureExporter();
        using var host = CreateHost("catalog-01", exporter);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var publisher = host.Services.GetRequiredService<ServiceMantleMetrics>();
        publisher.SetPhase(ServiceStartupPhase.Completed);
        var exception = Assert.Throws<ArgumentOutOfRangeException>(() => publisher.SetPhase((ServiceStartupPhase)invalid));
        Assert.Null(exception.ActualValue);
        AssertPhase(host.Services.GetRequiredService<MeterProvider>(), exporter, "completed");
    }

    [Fact]
    public async Task Concurrent_phase_updates_and_collections_remain_one_hot_and_bounded()
    {
        var exporter = new CaptureExporter();
        using var host = CreateHost("catalog-01", exporter);
        await host.StartAsync(TestContext.Current.CancellationToken);
        var publisher = host.Services.GetRequiredService<ServiceMantleMetrics>();
        var provider = host.Services.GetRequiredService<MeterProvider>();
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var writer = Task.Run(() =>
        {
            started.TrySetResult();
            var index = 0;
            while (!cancellation.IsCancellationRequested)
            {
                publisher.SetPhase((ServiceStartupPhase)(index++ % 3));
                if (index == 1000) index = 0;
            }
        }, cancellation.Token);
        await started.Task.WaitAsync(TestContext.Current.CancellationToken);
        try
        {
            for (var index = 0; index < 100; index++)
            {
                Assert.True(provider.ForceFlush());
                var phases = exporter.Points.Where(point => point.Name == ServiceMantleMetrics.InstallationPhaseName).ToArray();
                Assert.Equal(4, phases.Length);
                Assert.Equal(1, phases.Sum(point => point.Value));
                Assert.All(phases, point => Assert.InRange(point.Value, 0, 1));
            }
        }
        finally
        {
            cancellation.Cancel();
            await writer;
        }
        Assert.Equal(5, exporter.SeenSeries.Count);
    }

    [Fact]
    public async Task Disposing_one_host_closes_only_its_publisher()
    {
        var firstExporter = new CaptureExporter();
        var secondExporter = new CaptureExporter();
        var first = CreateHost("catalog-01", firstExporter);
        using var second = CreateHost("catalog-02", secondExporter);
        await first.StartAsync(TestContext.Current.CancellationToken);
        await second.StartAsync(TestContext.Current.CancellationToken);
        var publisher = first.Services.GetRequiredService<ServiceMantleMetrics>();
        await first.StopAsync(TestContext.Current.CancellationToken);
        first.Dispose();
        Assert.Throws<ObjectDisposedException>(() => publisher.SetPhase(ServiceStartupPhase.Completed));
        Assert.Throws<ObjectDisposedException>(publisher.SetUnknown);
        publisher.Dispose();
        AssertPhase(second.Services.GetRequiredService<MeterProvider>(), secondExporter, "unknown");
    }

    private static IHost CreateHost(string instanceId, CaptureExporter exporter, bool repeat = false)
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var service = builder.Services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse(instanceId), serviceVersion: "1.2.3");
        service.AddServiceMantleMetrics();
        if (repeat) service.AddServiceMantleMetrics();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(
            new PeriodicExportingMetricReader(exporter, exportIntervalMilliseconds: int.MaxValue)));
        return builder.Build();
    }

    private static void AssertPhase(MeterProvider provider, CaptureExporter exporter, string expected)
    {
        Assert.True(provider.ForceFlush());
        Assert.Equal(5, exporter.Points.Length);
        var info = Assert.Single(exporter.Points, point => point.Name == ServiceMantleMetrics.ServiceInfoName);
        Assert.Equal(1, info.Value);
        Assert.Empty(info.Tags);
        var phases = exporter.Points.Where(point => point.Name == ServiceMantleMetrics.InstallationPhaseName).ToArray();
        Assert.Equal(new[] { "bootstrap_configuration", "completed", "pending_setup", "unknown" },
            phases.Select(point => (string)point.Tags["phase"]!).Order());
        Assert.All(phases, point => Assert.Equal((string)point.Tags["phase"]! == expected ? 1 : 0, point.Value));
    }

    private sealed record Point(string Name, string? Unit, string MeterName, string? MeterVersion,
        long Value, Dictionary<string, object?> Tags);
    private sealed class CaptureExporter : BaseExporter<Metric>
    {
        public Point[] Points { get; private set; } = [];
        public ConcurrentDictionary<string, byte> SeenSeries { get; } = new();
        public override ExportResult Export(in Batch<Metric> batch)
        {
            var points = new List<Point>();
            foreach (var metric in batch)
            {
                if (metric.MeterName != ServiceMantleMetrics.MeterName) continue;
                foreach (ref readonly var point in metric.GetMetricPoints())
                {
                    var tags = new Dictionary<string, object?>();
                    foreach (var tag in point.Tags) tags.Add(tag.Key, tag.Value);
                    points.Add(new Point(metric.Name, metric.Unit, metric.MeterName, metric.MeterVersion,
                        point.GetGaugeLastValueLong(), tags));
                    SeenSeries.TryAdd(metric.Name + ":" + string.Join(",", tags.Select(pair => pair.Key + "=" + pair.Value)), 0);
                }
            }
            Points = points.ToArray();
            return ExportResult.Success;
        }
    }
}
