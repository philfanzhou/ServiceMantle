using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle;
using ServiceMantle.Configuration;
using ServiceMantle.Web;
using ServiceMantle.Diagnostics.Export.Otlp;
using Xunit;

namespace ServiceMantle.Diagnostics.Export.Otlp.Tests;

public sealed class OtlpSettingTests
{
    // ---------- classification ----------

    [Theory]
    [InlineData(null, null, "disabled", false)]
    [InlineData("https://otlp.example.test", null, "enabled", true)]
    [InlineData("https://otlp.example.test:4317", null, "enabled", true)]
    [InlineData("http://otlp.example.test", null, "endpoint_invalid", false)]
    [InlineData("https://user@otlp.example.test", null, "endpoint_invalid", false)]
    [InlineData("https://otlp.example.test?q=1", null, "endpoint_invalid", false)]
    [InlineData("otlp.example.test", null, "endpoint_invalid", false)]
    [InlineData("  ", null, "disabled", false)]
    public void Classification_is_deterministic_and_value_free(
        string? endpoint,
        string? _,
        string expectedCategory,
        bool expectedUsable)
    {
        var state = OtlpSettingState.Classify(endpoint);

        Assert.Equal(expectedCategory, state.Category);
        Assert.Equal(expectedUsable, state.Endpoint is not null);
        Assert.Equal(!expectedUsable && expectedCategory == "endpoint_invalid", state.IsUnusable);
        if (endpoint is not null && expectedCategory != "disabled")
        {
            Assert.DoesNotContain(endpoint, state.ToString(), StringComparison.Ordinal);
        }
    }

    // ---------- strict management-update validation ----------

    [Fact]
    public void Update_validation_accepts_empty_and_usable_endpoints_only()
    {
        var registry = new ServiceSettingDefinitionRegistry(
            [new OtlpSettingDefinitions()],
            [new OtlpSettingDefinitions()]);

        var empty = registry.Validate(new Dictionary<string, string?>());
        var usable = registry.Validate(new Dictionary<string, string?>
        {
            [OtlpSettingDefinitions.Endpoint] = "https://otlp.example.test"
        });
        var unusable = registry.Validate(new Dictionary<string, string?>
        {
            [OtlpSettingDefinitions.Endpoint] = "http://otlp.example.test"
        });

        Assert.True(empty.IsValid);
        Assert.True(usable.IsValid);
        Assert.False(unusable.IsValid);
        Assert.Contains(unusable.Errors, error =>
            error.Key == OtlpSettingDefinitions.Endpoint &&
            error.ErrorCode == WellKnownOtlpErrorCodes.InvalidEndpoint);
        Assert.DoesNotContain(
            "otlp.example.test",
            string.Join(",", unusable.Errors.Select(error => error.ToString())),
            StringComparison.Ordinal);
    }

    // ---------- setting-driven registration ----------

    [Fact]
    public async Task Usable_snapshot_enables_the_exporters_with_the_classified_endpoint()
    {
        var (builder, serviceMantle) = CreateHostBuilder();
        var snapshot = await LoadSnapshotAsync("https://otlp.example.test");

        var state = serviceMantle.AddOpenTelemetryOtlpExporterFromSettings(
            snapshot,
            options => options.Traces.Enabled = true);

        Assert.NotNull(state.Endpoint);
        Assert.False(state.IsUnusable);
        using var provider = builder.Services.BuildServiceProvider();
        Assert.NotNull(provider.GetService<OtlpRuntime>());
        Assert.Contains(provider.GetServices<IServiceSettingDefinitionProvider>(),
            candidate => candidate is OtlpSettingDefinitions);
        Assert.Contains(provider.GetServices<IServiceSettingCompositeValidator>(),
            candidate => candidate is OtlpSettingDefinitions);

        // The single registered trace provider carries the classified endpoint, not a
        // consumer-supplied one: the classification owns the endpoint.
        var registrations = provider.GetServices<OtlpRegistration>();
        Assert.Contains(registrations, registration =>
            registration.Traces.Enabled &&
            registration.Traces.Endpoint!.ToString() == "https://otlp.example.test/");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("http://otlp.example.test")]
    public async Task Empty_or_unusable_snapshot_registers_no_exporter_machinery(string? endpoint)
    {
        var (builder, serviceMantle) = CreateHostBuilder();
        var snapshot = await LoadSnapshotAsync(endpoint);

        var state = serviceMantle.AddOpenTelemetryOtlpExporterFromSettings(
            snapshot,
            options => options.Traces.Enabled = true);

        Assert.Null(state.Endpoint);
        using var provider = builder.Services.BuildServiceProvider();
        Assert.Null(provider.GetService<OtlpRuntime>());
        // No OpenTelemetry provider or hosted validator was registered either.
        Assert.DoesNotContain(provider.GetServices<IHostedService>(),
            service => service.GetType().Name == "OtlpStartupValidator");
        Assert.Empty(provider.GetServices<OpenTelemetry.Trace.TracerProvider>());
        if (endpoint is not null)
        {
            Assert.True(state.IsUnusable);
            Assert.DoesNotContain(endpoint, state.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Repeated_setting_driven_registration_stays_idempotent()
    {
        var (builder, serviceMantle) = CreateHostBuilder();
        var snapshot = await LoadSnapshotAsync("https://otlp.example.test");

        serviceMantle.AddOpenTelemetryOtlpExporterFromSettings(
            snapshot, options => options.Traces.Enabled = true);
        serviceMantle.AddOpenTelemetryOtlpExporterFromSettings(
            snapshot, options => options.Traces.Enabled = true);

        using var provider = builder.Services.BuildServiceProvider();
        Assert.Single(provider.GetServices<OtlpRuntime>());
        Assert.Single(provider.GetServices<OpenTelemetry.Trace.TracerProvider>());
    }

    private static (HostApplicationBuilder Builder, ServiceMantleBuilder ServiceMantle) CreateHostBuilder()
    {
        var builder = Host.CreateEmptyApplicationBuilder(new HostApplicationBuilderSettings());
        var serviceMantle = builder.Services.AddServiceMantle(
            ServiceId.Parse("otlp-settings-test"),
            InstanceId.Parse("otlp-settings-test-01"),
            serviceVersion: "1.2.3");
        return (builder, serviceMantle);
    }

    private static async Task<ServiceSettingSnapshot> LoadSnapshotAsync(string? endpoint)
    {
        var serviceId = ServiceId.Parse("otlp-settings-test");
        var registry = new ServiceSettingDefinitionRegistry([new OtlpSettingDefinitions()]);
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var values = new List<PersistedServiceSettingValue>();
        if (endpoint is not null)
        {
            values.Add(new PersistedServiceSettingValue(
                OtlpSettingDefinitions.Endpoint, 1, ServiceSettingValueType.String, endpoint));
        }

        var loader = new ServiceSettingSnapshotLoader(
            serviceId,
            new FixedSource(serviceId, values),
            registry,
            accessor);
        var result = await loader.RefreshAsync();
        Assert.True(result.Succeeded, $"snapshot load failed: {string.Join(", ", result.Errors)}");
        Assert.True(accessor.TryGetCurrent(out var snapshot));
        return snapshot!;
    }

    private sealed class FixedSource(
        ServiceId serviceId,
        IReadOnlyList<PersistedServiceSettingValue> values) : IServiceSettingSnapshotSource
    {
        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId requestedServiceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ServiceSettingSnapshotRead(serviceId, 1, values));
    }
}
