using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Configuration;
using Xunit;
using static ServiceMantle.Consul.ConsulSettingDefinitions;

namespace ServiceMantle.Consul.Tests;

/// <summary>
/// The typed snapshot-source registration entry: the Consul boundary reads exactly one dedicated
/// accessor type, the global catalog and accessor stay untouched, nothing is resolved before
/// CreateClient's safe boundary, and every conflict is rejected before background work starts.
/// </summary>
public sealed class ConsulTypedSnapshotSourceTests
{
    /// <summary>Counts reads and DI resolutions; publishes whatever its inner accessor holds.</summary>
    internal sealed class CountingAccessor : IServiceSettingCurrentSnapshotAccessor
    {
        internal int Reads;
        internal ServiceSettingCurrentSnapshotAccessor Inner { get; set; } = new();

        public bool TryGetCurrent(out ServiceSettingSnapshot? snapshot)
        {
            Reads++;
            return Inner.TryGetCurrent(out snapshot);
        }
    }

    internal sealed class OtherAccessor : IServiceSettingCurrentSnapshotAccessor
    {
        public bool TryGetCurrent(out ServiceSettingSnapshot? snapshot)
        {
            snapshot = null;
            return false;
        }
    }

    internal sealed class ThrowingAccessor : IServiceSettingCurrentSnapshotAccessor
    {
        internal const string Canary = "typed-accessor-factory-canary";

        public bool TryGetCurrent(out ServiceSettingSnapshot? snapshot) =>
            throw new InvalidOperationException(Canary);
    }

    private sealed class ProductDefinitions : IServiceSettingDefinitionProvider, IServiceSettingCompositeValidator
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() => [];
        public IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context) => [];
    }

    private sealed class RootKeySource : IServiceSettingRootKeySource
    {
        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(ConsulFixture.RootKey);
    }

    [Fact]
    public async Task A_disabled_typed_snapshot_wins_even_when_the_global_source_is_enabled()
    {
        var global = await ActivateAsync(ConsulFixture.Enabled());
        var typed = new CountingAccessor { Inner = await ActivateAsync(new()) };

        var (provider, factory) = Build(services =>
        {
            services.AddSingleton<IServiceSettingCurrentSnapshotAccessor>(global);
            services.AddSingleton(typed);
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        Assert.Null(provider.GetRequiredService<ConsulClientProvider>().CreateClient());
        Assert.Equal(1, typed.Reads);
        Assert.Equal(0, factory.Resolutions);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task An_enabled_typed_snapshot_creates_a_session_when_the_global_source_is_disabled()
    {
        var global = await ActivateAsync(new());
        var typed = new CountingAccessor { Inner = await ActivateAsync(ConsulFixture.Enabled(), version: 7) };

        var (provider, factory) = Build(services =>
        {
            services.AddSingleton<IServiceSettingCurrentSnapshotAccessor>(global);
            services.AddSingleton(typed);
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        using var session = provider.GetRequiredService<ConsulClientProvider>().CreateClient();
        Assert.NotNull(session);
        Assert.Equal(7, session!.SnapshotVersion);
        Assert.Equal(1, typed.Reads);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public void The_typed_entry_leaves_the_global_catalog_and_accessor_descriptors_alone()
    {
        var global = new ServiceSettingCurrentSnapshotAccessor();
        var product = new ProductDefinitions();
        var (provider, _) = Build(services =>
        {
            services.AddSingleton<IServiceSettingDefinitionProvider>(product);
            services.AddSingleton<IServiceSettingCompositeValidator>(product);
            services.AddSingleton<IServiceSettingCurrentSnapshotAccessor>(global);
            services.AddSingleton(new CountingAccessor());
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        Assert.Same(product, provider.GetRequiredService<IServiceSettingDefinitionProvider>());
        Assert.Same(product, provider.GetRequiredService<IServiceSettingCompositeValidator>());
        Assert.Same(global, provider.GetRequiredService<IServiceSettingCurrentSnapshotAccessor>());
    }

    [Fact]
    public void Registration_build_and_owner_resolution_never_resolve_the_typed_accessor()
    {
        var typed = new CountingAccessor();
        var resolutions = 0;
        var (provider, factory) = Build(services =>
        {
            services.AddSingleton<CountingAccessor>(_ =>
            {
                resolutions++;
                return typed;
            });
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        _ = provider.GetRequiredService<ConsulRegistrationLifecycle>();

        Assert.Equal(0, resolutions);
        Assert.Equal(0, typed.Reads);
        Assert.Equal(0, factory.Resolutions);
        Assert.Equal(0, factory.Calls);
    }

    [Fact]
    public async Task Start_reads_the_typed_source_exactly_once()
    {
        var typed = new CountingAccessor { Inner = await ActivateAsync(ConsulFixture.Enabled()) };
        var (provider, _) = Build(services =>
        {
            services.AddSingleton(typed);
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        var lifecycle = provider.GetRequiredService<ConsulRegistrationLifecycle>();
        Assert.Equal(0, typed.Reads);
        await lifecycle.StartAsync(CancellationToken.None);
        Assert.Equal(1, typed.Reads);
        Assert.Equal(ConsulLifecycleState.NotReady, lifecycle.State);
        await lifecycle.StopAsync(CancellationToken.None);
        await lifecycle.DisposeAsync();
    }

    [Fact]
    public void A_missing_typed_registration_is_the_closed_snapshot_unavailable_category()
    {
        var (provider, factory) = Build(services =>
            services.AddServiceMantleConsul<CountingAccessor>());

        _ = provider.GetRequiredService<ConsulRegistrationLifecycle>();
        var exception = Assert.Throws<ConsulConfigurationException>(
            () => provider.GetRequiredService<ConsulClientProvider>().CreateClient());

        Assert.Equal(ConsulConfigurationError.SnapshotUnavailable, exception.Error);
        Assert.Null(exception.InnerException);
        Assert.Equal(0, factory.Resolutions);
    }

    [Fact]
    public void A_throwing_typed_accessor_maps_to_snapshot_unavailable_without_the_canary()
    {
        var (provider, _) = Build(services =>
        {
            services.AddSingleton(new ThrowingAccessor());
            services.AddServiceMantleConsul<ThrowingAccessor>();
        });

        var exception = Assert.Throws<ConsulConfigurationException>(
            () => provider.GetRequiredService<ConsulClientProvider>().CreateClient());

        Assert.Equal(ConsulConfigurationError.SnapshotUnavailable, exception.Error);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(ThrowingAccessor.Canary, exception.ToString());
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public void Mixing_default_and_typed_registrations_is_rejected_in_both_orders(int order)
    {
        var (provider, _) = Build(services =>
        {
            if (order == 1)
            {
                services.AddServiceMantleConsul();
                services.AddServiceMantleConsul<CountingAccessor>();
            }
            else
            {
                services.AddServiceMantleConsul<CountingAccessor>();
                services.AddServiceMantleConsul();
            }
        });

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration,
            Assert.Throws<ConsulConfigurationException>(
                () => provider.GetRequiredService<ConsulRegistrationLifecycle>()).Error);
    }

    [Fact]
    public void Two_different_typed_accessor_types_are_a_conflict()
    {
        var (provider, _) = Build(services =>
        {
            services.AddSingleton(new CountingAccessor());
            services.AddSingleton(new OtherAccessor());
            services.AddServiceMantleConsul<CountingAccessor>();
            services.AddServiceMantleConsul<OtherAccessor>();
        });

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration,
            Assert.Throws<ConsulConfigurationException>(
                () => provider.GetRequiredService<ConsulRegistrationLifecycle>()).Error);
    }

    [Fact]
    public async Task Repeated_typed_registration_with_the_same_type_and_options_is_idempotent()
    {
        var typed = new CountingAccessor { Inner = await ActivateAsync(ConsulFixture.Enabled()) };
        var (provider, factory) = Build(services =>
        {
            services.AddSingleton(typed);
            services.AddServiceMantleConsul<CountingAccessor>();
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        Assert.Single(provider.GetServices<IHostedService>());
        _ = provider.GetRequiredService<ConsulRegistrationLifecycle>();
        using var session = provider.GetRequiredService<ConsulClientProvider>().CreateClient();
        Assert.NotNull(session);
        Assert.Equal(1, factory.Calls);
    }

    [Fact]
    public void Disagreeing_typed_timing_is_rejected_at_lifecycle_resolution()
    {
        var (provider, _) = Build(services =>
        {
            services.AddSingleton(new CountingAccessor());
            services.AddServiceMantleConsul<CountingAccessor>(
                options => options.ReadinessPollInterval = TimeSpan.FromSeconds(2));
            services.AddServiceMantleConsul<CountingAccessor>(
                options => options.ReadinessPollInterval = TimeSpan.FromSeconds(3));
        });

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration,
            Assert.Throws<ConsulConfigurationException>(
                () => provider.GetRequiredService<ConsulRegistrationLifecycle>()).Error);
    }

    [Fact]
    public void Disagreeing_typed_advertisements_are_rejected_at_lifecycle_resolution()
    {
        var (provider, _) = Build(services =>
        {
            services.AddSingleton(new CountingAccessor());
            services.AddServiceMantleConsul<CountingAccessor>(null, options =>
            {
                options.Address = "10.0.0.7";
                options.Port = 5001;
            });
            services.AddServiceMantleConsul<CountingAccessor>(null, options =>
            {
                options.Address = "10.0.0.8";
                options.Port = 5002;
            });
        });

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration,
            Assert.Throws<ConsulConfigurationException>(
                () => provider.GetRequiredService<ConsulRegistrationLifecycle>()).Error);
    }

    [Fact]
    public async Task A_typed_snapshot_for_another_service_is_rejected_at_the_consumer_boundary()
    {
        var typed = new CountingAccessor
        {
            Inner = await ActivateAsync(new(), ServiceId.Parse("other"))
        };
        var (provider, factory) = Build(services =>
        {
            services.AddSingleton(typed);
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        var exception = Assert.Throws<ConsulConfigurationException>(
            () => provider.GetRequiredService<ConsulClientProvider>().CreateClient());

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, exception.Error);
        Assert.Equal(0, factory.Resolutions);
    }

    [Fact]
    public async Task A_typed_snapshot_with_a_weakened_token_definition_is_rejected()
    {
        var typed = new CountingAccessor
        {
            Inner = await ActivateAsync(ConsulFixture.Enabled(), composite: false, tokenSensitive: false)
        };
        var (provider, factory) = Build(services =>
        {
            services.AddSingleton(typed);
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        var exception = Assert.Throws<ConsulConfigurationException>(
            () => provider.GetRequiredService<ConsulClientProvider>().CreateClient());

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, exception.Error);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(ConsulFixture.Secret, exception.ToString());
        Assert.Equal(0, factory.Resolutions);
    }

    [Fact]
    public async Task An_invalid_typed_endpoint_is_rejected_without_leaking_the_secret()
    {
        var raw = ConsulFixture.Enabled();
        raw[Endpoint] = "http://agent.example:8500";
        var typed = new CountingAccessor { Inner = await ActivateAsync(raw, composite: false) };
        var (provider, factory) = Build(services =>
        {
            services.AddSingleton(typed);
            services.AddServiceMantleConsul<CountingAccessor>();
        });

        var exception = Assert.Throws<ConsulConfigurationException>(
            () => provider.GetRequiredService<ConsulClientProvider>().CreateClient());

        Assert.Equal(ConsulConfigurationError.InvalidConfiguration, exception.Error);
        Assert.DoesNotContain(ConsulFixture.Secret, exception.ToString());
        Assert.Equal(0, factory.Resolutions);
    }

    [Fact]
    public async Task A_cancelled_start_outranks_a_failing_typed_accessor()
    {
        var (provider, _) = Build(services =>
        {
            services.AddSingleton(new ThrowingAccessor());
            services.AddServiceMantleConsul<ThrowingAccessor>();
        });

        var lifecycle = provider.GetRequiredService<ConsulRegistrationLifecycle>();
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => lifecycle.StartAsync(cancelled.Token));
        Assert.Equal(cancelled.Token, exception.CancellationToken);
        await lifecycle.DisposeAsync();
    }

    private static (ServiceProvider Provider, ConsulFixture.Factory Factory) Build(
        Action<ServiceCollection> configure)
    {
        var factory = new ConsulFixture.Factory();
        var services = new ServiceCollection();
        services.AddSingleton(ConsulFixture.Service);
        services.AddSingleton(ConsulFixture.Instance);
        services.AddSingleton<IConsulClientFactory>(_ =>
        {
            factory.Resolutions++;
            return factory;
        });
        configure(services);
        return (services.BuildServiceProvider(), factory);
    }

    /// <summary>
    /// Activates one complete snapshot into a standalone accessor through the real loader, the same
    /// fixture-shaped pipeline, so typed accessors publish exactly what production publishes.
    /// </summary>
    private static async Task<ServiceSettingCurrentSnapshotAccessor> ActivateAsync(
        Dictionary<string, string?> raw,
        ServiceId? service = null,
        long version = 1,
        bool composite = true,
        bool tokenSensitive = true)
    {
        var serviceId = service ?? ConsulFixture.Service;
        var definitions = new ConsulSettingDefinitions().GetDefinitions().Select(d =>
            d.Key == ConsulSettingDefinitions.Token && !tokenSensitive
                ? new ServiceSettingDefinition(d.Key, ServiceSettingValueType.String)
                : d).ToArray();
        var registry = new ServiceSettingDefinitionRegistry(
            [new ConsulFixture.Definitions(definitions)],
            composite ? [new ConsulSettingDefinitions()] : []);
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new ConsulFixture.Source();
        using var loader = new ServiceSettingSnapshotLoader(serviceId, source, registry, accessor, new RootKeySource());
        source.Read = new(serviceId, version, raw.Select(pair =>
        {
            Assert.True(registry.TryGetDefinition(pair.Key, out var definition));
            var value = definition!.IsSensitive
                ? new SensitiveValueProtector(serviceId, definition.Key).Protect(pair.Value!, ConsulFixture.RootKey)
                : pair.Value!;
            return new PersistedServiceSettingValue(pair.Key, version, definition.ValueType, value);
        }));
        var result = await loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        return accessor;
    }
}
