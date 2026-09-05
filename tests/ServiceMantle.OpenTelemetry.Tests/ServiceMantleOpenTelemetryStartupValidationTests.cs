using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using OpenTelemetry;
using OpenTelemetry.Metrics;
using OpenTelemetry.Trace;
using ServiceMantle.AspNetCore;
using Xunit;

namespace ServiceMantle.OpenTelemetry.Tests;

public sealed class ServiceMantleOpenTelemetryStartupValidationTests
{
    private const int Disabled = 8;
    private const string Secret = "unrelated-telemetry-validation-test-secret";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    public static TheoryData<bool, int, int> InvalidRegistrations
    {
        get
        {
            var cases = new TheoryData<bool, int, int>();
            foreach (var webApplication in new[] { false, true })
            {
                // Bits 1/2/4 select ASP.NET Core/HttpClient/runtime; 0 is invalid enabled,
                // and 8 is disabled. Exercise every pair of different effective settings.
                for (var first = 0; first <= Disabled; first++)
                for (var second = 0; second <= Disabled; second++)
                {
                    if (first != second || first == 0)
                        cases.Add(webApplication, first, second);
                }
            }
            return cases;
        }
    }

    public static TheoryData<bool, int> ValidRegistrations
    {
        get
        {
            var cases = new TheoryData<bool, int>();
            foreach (var webApplication in new[] { false, true })
            for (var selection = 1; selection <= Disabled; selection++)
                cases.Add(webApplication, selection);
            return cases;
        }
    }

    [Theory]
    [MemberData(nameof(InvalidRegistrations))]
    public async Task Invalid_final_configuration_fails_before_either_instrumentation_factory(
        bool webApplication, int first, int second)
    {
        var builder = CreateBuilder(webApplication);
        var mantle = AddIdentity(builder);
        Register(mantle, first);
        Register(mantle, second);
        var tracing = new InstrumentationLifetime();
        var metrics = new InstrumentationLifetime();
        ObserveFactories(builder.Services, tracing, metrics);

        using var host = Build(builder);
        Assert.Equal(0, tracing.Created);
        Assert.Equal(0, metrics.Created);

        // Do not resolve providers: normal Host startup must enforce this boundary,
        // including framework DI/Options that materializes MeterProvider early.
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => host.StartAsync(Token));

        Assert.Contains(first == 0 || second == 0 ? "at least one" : "conflicting", error.Message,
            StringComparison.Ordinal);
        Assert.DoesNotContain(Secret, error.ToString(), StringComparison.Ordinal);
        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, tracing.Created);
        Assert.Equal(0, metrics.Created);
    }

    [Theory]
    [MemberData(nameof(ValidRegistrations))]
    public async Task Equivalent_registrations_keep_signal_selection_resource_and_lifecycle(
        bool webApplication, int selection)
    {
        var builder = CreateBuilder(webApplication);
        var mantle = AddIdentity(builder);
        Register(mantle, selection);
        if (selection == Disabled)
        {
            // Disabled signal flags do not affect the effective configuration.
            mantle.AddOpenTelemetryInstrumentation(options => options.Enabled = false);
        }
        else
        {
            Register(mantle, selection);
        }
        var tracing = new InstrumentationLifetime();
        var metrics = new InstrumentationLifetime();
        ObserveFactories(builder.Services, tracing, metrics);
        var hasTracing = selection != Disabled && (selection & 3) != 0;
        var hasMetrics = selection != Disabled && (selection & 4) != 0;

        using (var host = Build(builder))
        {
            Assert.Equal(0, tracing.Created);
            Assert.Equal(0, metrics.Created);
            await host.StartAsync(Token);

            AssertProvider(host.Services.GetServices<TracerProvider>(), hasTracing);
            AssertProvider(host.Services.GetServices<MeterProvider>(), hasMetrics);
            Assert.Equal(hasTracing ? 1 : 0, tracing.Created);
            Assert.Equal(hasMetrics ? 1 : 0, metrics.Created);
            await host.StopAsync(Token);
        }

        Assert.Equal(hasTracing ? 1 : 0, tracing.Disposed);
        Assert.Equal(hasMetrics ? 1 : 0, metrics.Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Precancelled_start_remains_cancellation_without_activating_instrumentation(bool webApplication)
    {
        var builder = CreateBuilder(webApplication);
        Register(AddIdentity(builder), 7);
        var tracing = new InstrumentationLifetime();
        var metrics = new InstrumentationLifetime();
        ObserveFactories(builder.Services, tracing, metrics);
        using var host = Build(builder);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => host.StartAsync(cancellation.Token));

        Assert.False(host.Services.GetRequiredService<IHostApplicationLifetime>()
            .ApplicationStarted.IsCancellationRequested);
        Assert.Equal(0, tracing.Created);
        Assert.Equal(0, metrics.Created);
    }

    private static IHostApplicationBuilder CreateBuilder(bool webApplication)
    {
        IHostApplicationBuilder builder;
        if (webApplication)
        {
            var web = WebApplication.CreateSlimBuilder();
            web.WebHost.UseUrls("http://127.0.0.1:0");
            builder = web;
        }
        else
        {
            builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        }
        builder.Logging.ClearProviders();
        builder.Configuration["Unrelated:Password"] = Secret;
        return builder;
    }

    private static IHost Build(IHostApplicationBuilder builder) => builder switch
    {
        WebApplicationBuilder web => web.Build(),
        HostApplicationBuilder generic => generic.Build(),
        _ => throw new InvalidOperationException("Unexpected test host builder."),
    };

    private static ServiceMantleBuilder AddIdentity(IHostApplicationBuilder builder) =>
        builder.Services.AddServiceMantle(ServiceId.Parse("catalog"), InstanceId.Parse("catalog-01"),
            serviceVersion: "1.2.3");

    private static void Register(ServiceMantleBuilder mantle, int selection) =>
        mantle.AddOpenTelemetryInstrumentation(options =>
        {
            options.Enabled = selection != Disabled;
            options.EnableAspNetCoreTracing = (selection & 1) != 0;
            options.EnableHttpClientTracing = (selection & 2) != 0;
            options.EnableRuntimeMetrics = (selection & 4) != 0;
        });

    private static void ObserveFactories(IServiceCollection services,
        InstrumentationLifetime tracing, InstrumentationLifetime metrics)
    {
        // Deferred configuration observes existing providers without registering absent signals.
        services.ConfigureOpenTelemetryTracerProvider((_, provider) =>
            provider.AddInstrumentation(tracing.Create));
        services.ConfigureOpenTelemetryMeterProvider((_, provider) =>
            provider.AddInstrumentation(metrics.Create));
    }

    private static void AssertProvider<T>(IEnumerable<T> providers, bool expected) where T : BaseProvider
    {
        if (!expected)
        {
            Assert.Empty(providers);
            return;
        }
        var attributes = Assert.Single(providers).GetResource().Attributes.ToDictionary(
            attribute => attribute.Key, attribute => attribute.Value, StringComparer.Ordinal);
        Assert.Equal(new Dictionary<string, object>
        {
            ["service.name"] = "catalog",
            ["service.version"] = "1.2.3",
            ["service.instance.id"] = "catalog-01",
        }, attributes);
    }

    private sealed class InstrumentationLifetime : IDisposable
    {
        internal int Created;
        internal int Disposed;

        internal InstrumentationLifetime Create()
        {
            Interlocked.Increment(ref Created);
            return this;
        }

        public void Dispose() => Interlocked.Increment(ref Disposed);
    }
}
