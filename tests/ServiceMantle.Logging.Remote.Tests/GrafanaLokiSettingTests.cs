using Microsoft.Extensions.DependencyInjection;
using System.Net;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Logging.Pipeline;
using ServiceMantle.Configuration;
using ServiceMantle.Logging;
using ServiceMantle.Logging.Pipeline;
using ServiceMantle.Logging.Remote;
using Xunit;
using static ServiceMantle.Logging.Remote.GrafanaLokiSettingStatus;

namespace ServiceMantle.Logging.Remote.Tests;

public sealed class GrafanaLokiSettingTests
{
    private const string SecretAuthorization = "Bearer secret-loki-token";
    private const string SecretRootKey = "test-root-key";

    // ---------- classification: every status, no sensitive rendering ----------

    public static IEnumerable<object[]> ClassificationCases()
    {
        yield return new object[] { null, null, Disabled, "disabled", null, null };
        yield return new object[]
        {
            "https://logs.example.test", SecretAuthorization, Enabled, "enabled",
            "https://logs.example.test/", null
        };
        yield return new object[]
        {
            "http://logs.example.test", SecretAuthorization, EndpointInvalid, "endpoint_invalid", null, null
        };
        yield return new object[]
        {
            "https://user:pass@logs.example.test", SecretAuthorization, EndpointInvalid,
            "endpoint_invalid", null, null
        };
        yield return new object[]
        {
            null, SecretAuthorization, EndpointMissing, "endpoint_missing", null, null
        };
        yield return new object[]
        {
            "https://logs.example.test", null, AuthorizationMissing, "authorization_missing", null, null
        };
        yield return new object[]
        {
            "https://logs.example.test", "bad\nvalue", AuthorizationInvalid,
            "authorization_invalid", null, null
        };
    }

    [Theory]
    [MemberData(nameof(ClassificationCases))]
    public void Every_classification_is_deterministic_and_value_free(
        string? endpoint,
        string? authorization,
        GrafanaLokiSettingStatus expectedStatus,
        string expectedCategory,
        string? expectedEndpoint,
        string? _)
    {
        var state = GrafanaLokiSettingState.Classify(endpoint, authorization);

        Assert.Equal(expectedStatus, state.Status);
        Assert.Equal(expectedCategory, state.Category);
        Assert.Equal(expectedEndpoint, state.Endpoint?.ToString());
        if (expectedStatus != Enabled)
        {
            Assert.Null(state.Authorization);
        }

        Assert.DoesNotContain(SecretAuthorization, state.ToString(), StringComparison.Ordinal);
        if (endpoint is not null)
        {
            Assert.DoesNotContain(endpoint, state.ToString(), StringComparison.Ordinal);
        }
        if (expectedStatus is not (Disabled or Enabled))
        {
            Assert.True(state.IsUnusable);
        }
        else
        {
            Assert.False(state.IsUnusable);
        }
    }

    [Theory]
    [InlineData("https://logs.example.test", true)]
    [InlineData("https://logs.example.test/prefix", true)]
    [InlineData("http://logs.example.test", false)]
    [InlineData("https://user@logs.example.test", false)]
    [InlineData("https://logs.example.test?q=1", false)]
    [InlineData("https://logs.example.test#f", false)]
    [InlineData("logs.example.test", false)]
    [InlineData("  ", false)]
    public void Endpoint_rule_matches_the_sink_rule(string candidate, bool expected)
    {
        Assert.Equal(expected, GrafanaLokiSettingState.TryParseEndpoint(candidate, out _));
    }

    [Theory]
    [InlineData("Bearer x", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    [InlineData("a\nb", false)]
    public void Authorization_rule_rejects_unusable_values(string candidate, bool expected)
    {
        Assert.Equal(expected, GrafanaLokiSettingDefinitions.IsUsableAuthorization(candidate));
        Assert.False(GrafanaLokiSettingDefinitions.IsUsableAuthorization(
            new string('x', GrafanaLokiSettingDefinitions.MaximumAuthorizationLength + 1)));
    }

    // ---------- strict management-update validation ----------

    [Fact]
    public void Update_validation_accepts_a_usable_pair_and_an_empty_pair()
    {
        var registry = Registry();

        var usable = registry.Validate(new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.Endpoint] = "https://logs.example.test",
            [GrafanaLokiSettingDefinitions.Authorization] = SecretAuthorization
        });
        var empty = registry.Validate(new Dictionary<string, string?>());

        Assert.True(usable.IsValid);
        Assert.True(empty.IsValid);
    }

    [Fact]
    public void Update_validation_rejects_values_the_next_start_cannot_use()
    {
        var registry = Registry();

        var invalidEndpoint = registry.Validate(new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.Endpoint] = "http://logs.example.test",
            [GrafanaLokiSettingDefinitions.Authorization] = SecretAuthorization
        });
        Assert.False(invalidEndpoint.IsValid);
        Assert.Contains(invalidEndpoint.Errors, error =>
            error.Key == GrafanaLokiSettingDefinitions.Endpoint &&
            error.ErrorCode == WellKnownGrafanaLokiErrorCodes.InvalidEndpoint);

        var invalidAuthorization = registry.Validate(new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.Endpoint] = "https://logs.example.test",
            [GrafanaLokiSettingDefinitions.Authorization] = "bad\nvalue"
        });
        Assert.False(invalidAuthorization.IsValid);
        Assert.Contains(invalidAuthorization.Errors, error =>
            error.Key == GrafanaLokiSettingDefinitions.Authorization &&
            error.ErrorCode == WellKnownGrafanaLokiErrorCodes.AuthorizationValueInvalid);

        var halfConfigured = registry.Validate(new Dictionary<string, string?>
        {
            [GrafanaLokiSettingDefinitions.Endpoint] = "https://logs.example.test"
        });
        Assert.False(halfConfigured.IsValid);
        Assert.Contains(halfConfigured.Errors, error =>
            error.Key == GrafanaLokiSettingDefinitions.Authorization &&
            error.ErrorCode == WellKnownServiceSettingValidationErrorCodes.Required);

        Assert.All(
            invalidEndpoint.Errors.Concat(invalidAuthorization.Errors).Concat(halfConfigured.Errors),
            error =>
            {
                Assert.DoesNotContain(SecretAuthorization, error.ToString(), StringComparison.Ordinal);
                Assert.DoesNotContain("logs.example.test", error.ToString(), StringComparison.Ordinal);
            });
    }

    // ---------- snapshot classification ----------

    [Fact]
    public async Task Snapshot_classification_reads_the_stored_pair()
    {
        var enabledSnapshot = await LoadSnapshotAsync(
            "https://logs.example.test",
            SecretAuthorization);
        var state = GrafanaLokiSettingState.Classify(enabledSnapshot);

        Assert.Equal(Enabled, state.Status);
        Assert.Equal("https://logs.example.test/", state.Endpoint!.ToString());
        Assert.Equal(SecretAuthorization, state.Authorization);
        Assert.DoesNotContain(SecretAuthorization, state.ToString(), StringComparison.Ordinal);
    }

    // ---------- setting-driven registration ----------

    [Fact]
    public async Task Usable_snapshot_enables_the_sink_under_the_explicit_rules()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging();
        var snapshot = await LoadSnapshotAsync("https://logs.example.test", SecretAuthorization);

        var state = builder.AddServiceMantleGrafanaLokiFromSettings(snapshot);

        Assert.Equal(Enabled, state.Status);
        using var provider = builder.Services.BuildServiceProvider();
        var registration = provider.GetRequiredService<GrafanaLokiRegistration>();
        Assert.True(registration.Options.Enabled);
        Assert.Equal("https://logs.example.test/", registration.Options.Endpoint!.ToString());
        Assert.Equal(
            ServiceMantleGrafanaLokiHostApplicationBuilderExtensions
                .SettingDrivenAuthorizationResolverName,
            registration.Options.AuthorizationHeaderResolverName);

        var resolvers = provider.GetServices<IRemoteLogAuthorizationResolver>()
            .OfType<FixedRemoteLogAuthorizationResolver>()
            .ToList();
        var resolver = Assert.Single(resolvers);
        Assert.Equal(
            SecretAuthorization,
            resolver.ResolveAuthorizationHeader(
                ServiceMantleGrafanaLokiHostApplicationBuilderExtensions
                    .SettingDrivenAuthorizationResolverName));
        Assert.DoesNotContain(SecretAuthorization, resolver.ToString(), StringComparison.Ordinal);

        Assert.Contains(provider.GetServices<IServiceSettingDefinitionProvider>(),
            candidate => candidate is GrafanaLokiSettingDefinitions);
        Assert.Contains(provider.GetServices<IServiceSettingCompositeValidator>(),
            candidate => candidate is GrafanaLokiSettingDefinitions);
    }

    [Theory]
    [InlineData(null, null, Disabled)]
    [InlineData("http://logs.example.test", SecretAuthorization, EndpointInvalid)]
    [InlineData("https://logs.example.test", null, AuthorizationMissing)]
    public async Task Empty_or_unusable_snapshot_keeps_the_sink_disabled_without_a_resolver(
        string? endpoint,
        string? authorization,
        GrafanaLokiSettingStatus expectedStatus)
    {
        var builder = Host.CreateApplicationBuilder();
        builder.Services.AddLogging();
        var snapshot = await LoadSnapshotAsync(endpoint, authorization);

        var state = builder.AddServiceMantleGrafanaLokiFromSettings(
            snapshot,
            options => options.Enabled = true);

        Assert.Equal(expectedStatus, state.Status);
        using var provider = builder.Services.BuildServiceProvider();
        var registration = provider.GetRequiredService<GrafanaLokiRegistration>();
        Assert.False(registration.Options.Enabled);
        // No resolver was registered: nothing can look the authorization value up.
        Assert.DoesNotContain(provider.GetServices<IRemoteLogAuthorizationResolver>(),
            candidate => candidate is FixedRemoteLogAuthorizationResolver);
        Assert.DoesNotContain(SecretAuthorization, state.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Repeated_setting_driven_registration_stays_equivalent_and_startable()
    {
        // The underlying entry's idempotency rule: equivalent repeated registrations are
        // idempotent at host start, which is where multiple registrations are resolved.
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        builder.Services.Replace(ServiceDescriptor.Singleton<ILokiHttpMessageHandlerFactory>(
            new StaticHandlerFactory(new RecordingHandler())));
        var snapshot = await LoadSnapshotAsync("https://logs.example.test", SecretAuthorization);

        builder.AddServiceMantleGrafanaLokiFromSettings(snapshot);
        builder.AddServiceMantleGrafanaLokiFromSettings(snapshot);

        using var host = builder.Build();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        await host.StartAsync(cancellation.Token);
        var registrations = host.Services.GetServices<GrafanaLokiRegistration>().ToList();
        Assert.True(registrations.Count >= 1);
        Assert.All(registrations, registration =>
        {
            Assert.True(registration.Options.Enabled);
            Assert.Equal("https://logs.example.test/", registration.Options.Endpoint!.ToString());
        });
        await host.StopAsync(cancellation.Token);
    }

    [Fact]
    public async Task A_conflicting_explicit_registration_fails_at_start_under_the_existing_rules()
    {
        // The setting-driven entry inherits the explicit entry's conflict rule: an enabled
        // registration that disagrees with another enabled one fails the host start with the
        // fixed conflicting-registration error code.
        var builder = Host.CreateApplicationBuilder();
        builder.AddServiceMantleSerilog(options => options.FlushTimeout = TimeSpan.FromSeconds(5));
        builder.Services.Replace(ServiceDescriptor.Singleton<ILokiHttpMessageHandlerFactory>(
            new StaticHandlerFactory(new RecordingHandler())));
        var snapshot = await LoadSnapshotAsync("https://logs.example.test", SecretAuthorization);

        builder.AddServiceMantleGrafanaLoki(options =>
        {
            options.Enabled = true;
            options.Endpoint = new Uri("https://other.example.test");
            options.AuthorizationHeaderResolverName = "explicit-entry";
        });
        builder.AddServiceMantleGrafanaLokiFromSettings(snapshot);

        using var host = builder.Build();
        var exception = await Assert.ThrowsAsync<SerilogConfigurationException>(() =>
            host.StartAsync(TestContext.Current.CancellationToken));
        Assert.Equal(WellKnownGrafanaLokiErrorCodes.ConflictingRegistration, exception.ErrorCode);
    }

    private sealed class RecordingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }

    private sealed class StaticHandlerFactory(HttpMessageHandler handler) : ILokiHttpMessageHandlerFactory
    {
        public HttpMessageHandler Create() => handler;
    }

    private static ServiceSettingDefinitionRegistry Registry() => new(
        [new GrafanaLokiSettingDefinitions()],
        [new GrafanaLokiSettingDefinitions()]);

    private static async Task<ServiceSettingSnapshot> LoadSnapshotAsync(
        string? endpoint,
        string? authorization)
    {
        var serviceId = ServiceId.Parse("loki-settings-test");
        // The startup load deliberately does not attach the strict management-update validator,
        // exactly like a consumer that separates the two registries: values an older release
        // stored must still materialize so the classification can disable the sink with a warning.
        var registry = new ServiceSettingDefinitionRegistry([new GrafanaLokiSettingDefinitions()]);
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var protector = new SensitiveValueProtector(
            serviceId, GrafanaLokiSettingDefinitions.Authorization);
        var values = new List<PersistedServiceSettingValue>();
        if (endpoint is not null)
        {
            values.Add(new PersistedServiceSettingValue(
                GrafanaLokiSettingDefinitions.Endpoint, 1,
                ServiceSettingValueType.String, endpoint));
        }

        if (authorization is not null)
        {
            values.Add(new PersistedServiceSettingValue(
                GrafanaLokiSettingDefinitions.Authorization, 1,
                ServiceSettingValueType.String,
                protector.Protect(authorization, SecretRootKey)));
        }

        var loader = new ServiceSettingSnapshotLoader(
            serviceId,
            new FixedSource(serviceId, values),
            registry,
            accessor,
            rootKeySource: new FixedRootKeySource(SecretRootKey));
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

    private sealed class FixedRootKeySource(string rootKey) : IServiceSettingRootKeySource
    {
        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(rootKey);
    }
}
