using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle.OpenTelemetry;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Tests;

public sealed class ServiceMantleOpenTelemetryRegistrationTests
{
    [Fact]
    public async Task AddServiceMantle_alone_does_not_register_or_create_telemetry_providers()
    {
        var builder = CreateHostBuilder();
        builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.2.3");

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(host.Services.GetService<TracerProvider>());
        Assert.Null(host.Services.GetService<MeterProvider>());
    }

    [Fact]
    public async Task Explicitly_disabled_registration_has_no_provider_or_listener_lifecycle()
    {
        var builder = CreateHostBuilder();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.2.3")
            .AddOpenTelemetryInstrumentation(options => options.Enabled = false);
        using var aspNetCoreSource = new ActivitySource("Microsoft.AspNetCore");
        using var httpClientSource = new ActivitySource("System.Net.Http");
        using var runtimeMeter = new Meter("System.Runtime");
        var runtimeCounter = runtimeMeter.CreateCounter<long>("servicemantle.disabled.test");

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Null(host.Services.GetService<TracerProvider>());
        Assert.Null(host.Services.GetService<MeterProvider>());
        Assert.False(aspNetCoreSource.HasListeners());
        Assert.False(httpClientSource.HasListeners());
        Assert.False(runtimeCounter.Enabled);
    }

    [Fact]
    public async Task Enabled_registration_creates_both_providers_with_only_host_identity_resource_attributes()
    {
        var builder = CreateHostBuilder();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.2.3+build.4")
            .AddOpenTelemetryInstrumentation();

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        var tracerAttributes = Attributes(host.Services.GetRequiredService<TracerProvider>());
        var meterAttributes = Attributes(host.Services.GetRequiredService<MeterProvider>());
        var expected = new Dictionary<string, object>
        {
            ["service.name"] = "catalog",
            ["service.version"] = "1.2.3+build.4",
            ["service.instance.id"] = "catalog-01",
        };

        Assert.Equal(expected, tracerAttributes);
        Assert.Equal(expected, meterAttributes);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Signal_selection_creates_only_the_required_provider(bool tracingOnly)
    {
        var builder = CreateHostBuilder();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.2.3")
            .AddOpenTelemetryInstrumentation(options =>
            {
                options.EnableAspNetCoreTracing = tracingOnly;
                options.EnableHttpClientTracing = tracingOnly;
                options.EnableRuntimeMetrics = !tracingOnly;
            });

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Equal(tracingOnly, host.Services.GetService<TracerProvider>() is not null);
        Assert.Equal(!tracingOnly, host.Services.GetService<MeterProvider>() is not null);
    }

    [Fact]
    public async Task Enabled_instrumentations_attach_and_are_removed_when_the_host_is_disposed()
    {
        var builder = CreateHostBuilder();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.2.3")
            .AddOpenTelemetryInstrumentation();
        var exporter = new NoopMetricExporter();
        builder.Services.AddOpenTelemetry().WithMetrics(metrics => metrics.AddReader(
            new PeriodicExportingMetricReader(
                exporter,
                exportIntervalMilliseconds: 10,
                exportTimeoutMilliseconds: 1_000)));
        using var aspNetCoreSource = new ActivitySource("Microsoft.AspNetCore");
        using var httpClientSource = new ActivitySource("System.Net.Http");
        using var runtimeMeter = new Meter("System.Runtime");
        var runtimeCounter = runtimeMeter.CreateCounter<long>("servicemantle.lifecycle.test");
        var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        Assert.True(aspNetCoreSource.HasListeners());
        Assert.True(httpClientSource.HasListeners());
        Assert.True(runtimeCounter.Enabled);
        await WaitForExportAsync(exporter, TestContext.Current.CancellationToken);

        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();
        var exportCountAfterDisposal = exporter.ExportCount;
        await Task.Delay(50, TestContext.Current.CancellationToken);

        Assert.False(aspNetCoreSource.HasListeners());
        Assert.False(httpClientSource.HasListeners());
        Assert.False(runtimeCounter.Enabled);
        Assert.Equal(exportCountAfterDisposal, exporter.ExportCount);
    }

    [Fact]
    public async Task Equivalent_repeated_registration_is_idempotent()
    {
        var builder = CreateHostBuilder();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.2.3");

        serviceMantle.AddOpenTelemetryInstrumentation(options =>
            options.EnableHttpClientTracing = false);
        serviceMantle.AddOpenTelemetryInstrumentation(options =>
            options.EnableHttpClientTracing = false);

        using var host = builder.Build();
        await host.StartAsync(TestContext.Current.CancellationToken);

        Assert.Single(host.Services.GetServices<TracerProvider>());
        Assert.Single(host.Services.GetServices<MeterProvider>());
        Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(TracerProvider));
        Assert.Single(builder.Services, descriptor =>
            descriptor.ServiceType == typeof(MeterProvider));
    }

    [Fact]
    public async Task Conflicting_registration_fails_before_provider_instrumentation_is_created()
    {
        var builder = CreateHostBuilder();
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("catalog"),
            InstanceId.Parse("catalog-01"),
            serviceVersion: "1.2.3");
        serviceMantle.AddOpenTelemetryInstrumentation();
        serviceMantle.AddOpenTelemetryInstrumentation(options =>
            options.EnableRuntimeMetrics = false);
        var created = 0;
        builder.Services.AddOpenTelemetry().WithTracing(tracing => tracing.AddInstrumentation(() =>
        {
            Interlocked.Increment(ref created);
            return new RecordingInstrumentation("trace", new ConcurrentQueue<string>());
        }));

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("conflicting", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, Volatile.Read(ref created));
    }

    [Fact]
    public async Task Enabled_registration_without_an_instrumentation_fails_at_startup()
    {
        var builder = CreateHostBuilder();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.2.3")
            .AddOpenTelemetryInstrumentation(options =>
            {
                options.EnableAspNetCoreTracing = false;
                options.EnableHttpClientTracing = false;
                options.EnableRuntimeMetrics = false;
            });

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));

        Assert.Contains("at least one", exception.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Providers_release_in_reverse_registration_order()
    {
        var disposed = new ConcurrentQueue<string>();
        var builder = CreateHostBuilder();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.2.3")
            .AddOpenTelemetryInstrumentation();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddInstrumentation(
                () => new RecordingInstrumentation("metrics", disposed)))
            .WithTracing(tracing => tracing.AddInstrumentation(
                () => new RecordingInstrumentation("tracing", disposed)));
        var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        host.Dispose();

        Assert.Equal(["tracing", "metrics"], disposed);
    }

    [Fact]
    public async Task Provider_disposal_exception_is_observable()
    {
        var disposed = new ConcurrentQueue<string>();
        var builder = CreateHostBuilder();
        builder.Services
            .AddServiceMantle(
                ServiceId.Parse("catalog"),
                InstanceId.Parse("catalog-01"),
                serviceVersion: "1.2.3")
            .AddOpenTelemetryInstrumentation();
        builder.Services.AddOpenTelemetry()
            .WithMetrics(metrics => metrics.AddInstrumentation(
                () => new RecordingInstrumentation("metrics", disposed)))
            .WithTracing(tracing => tracing.AddInstrumentation(
                () => new RecordingInstrumentation("tracing", disposed, throwOnDispose: true)));
        var host = builder.Build();

        await host.StartAsync(TestContext.Current.CancellationToken);
        await host.StopAsync(TestContext.Current.CancellationToken);
        var tracerProvider = host.Services.GetRequiredService<TracerProvider>();
        var meterProvider = host.Services.GetRequiredService<MeterProvider>();
        try
        {
            var exception = Assert.ThrowsAny<Exception>(host.Dispose);

            Assert.Contains("test disposal failure", exception.ToString(), StringComparison.Ordinal);
            Assert.Equal(["tracing"], disposed);
        }
        finally
        {
            tracerProvider.Dispose();
            meterProvider.Dispose();
        }
    }

    private static HostApplicationBuilder CreateHostBuilder() =>
        Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());

    private static Dictionary<string, object> Attributes(BaseProvider provider) =>
        provider.GetResource().Attributes.ToDictionary(
            attribute => attribute.Key,
            attribute => attribute.Value,
            StringComparer.Ordinal);

    private static async Task WaitForExportAsync(
        NoopMetricExporter exporter,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(2);
        while (exporter.ExportCount == 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(10, cancellationToken);
        }

        Assert.NotEqual(0, exporter.ExportCount);
    }

    private sealed class RecordingInstrumentation(
        string name,
        ConcurrentQueue<string> disposed,
        bool throwOnDispose = false) : IDisposable
    {
        private int disposeCount;

        public void Dispose()
        {
            disposed.Enqueue(name);
            if (throwOnDispose && Interlocked.Increment(ref disposeCount) == 1)
            {
                throw new InvalidOperationException("test disposal failure");
            }
        }
    }

    private sealed class NoopMetricExporter : BaseExporter<Metric>
    {
        private int exportCount;

        internal int ExportCount => Volatile.Read(ref exportCount);

        public override ExportResult Export(in Batch<Metric> batch)
        {
            Interlocked.Increment(ref exportCount);
            return ExportResult.Success;
        }
    }
}
