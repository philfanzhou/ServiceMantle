using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using ServiceMantle.Consul;
using Xunit;

namespace ServiceMantle.Consul.Tests;

internal sealed class ConsulFixture : IDisposable
{
    internal const string Secret = "consul-secret-do-not-project";
    internal const string RootKey = "test-only-external-root-key";
    internal static readonly ServiceId Service = ServiceId.Parse("orders");
    internal static readonly InstanceId Instance = InstanceId.Parse("host/instance?one");
    internal readonly ServiceSettingCurrentSnapshotAccessor Accessor = new();
    internal readonly Source SnapshotSource = new();
    internal readonly ServiceSettingDefinitionRegistry Registry;
    internal readonly ServiceSettingSnapshotLoader Loader;
    internal readonly Factory ClientFactory = new();
    internal readonly ServiceProvider Services;

    internal ConsulFixture(bool composite = true, bool tokenSensitive = true, ServiceId? snapshotService = null,
        Action<IServiceCollection>? configureServices = null)
    {
        var definitions = new ConsulSettingDefinitions().GetDefinitions().Select(d =>
            d.Key == ConsulSettingDefinitions.Token && !tokenSensitive
                ? new ServiceSettingDefinition(d.Key, ServiceSettingValueType.String)
                : d).ToArray();
        Registry = new([new Definitions(definitions)], composite ? [new ConsulSettingDefinitions()] : []);
        Loader = new(snapshotService ?? Service, SnapshotSource, Registry, Accessor, new Root());
        var services = new ServiceCollection();
        services.AddSingleton(Service);
        services.AddSingleton(Instance);
        services.AddSingleton<IServiceSettingCurrentSnapshotAccessor>(Accessor);
        services.AddSingleton<IConsulClientFactory>(_ =>
        {
            ClientFactory.Resolutions++;
            return ClientFactory;
        });
        configureServices?.Invoke(services);
        services.AddServiceMantleConsul();
        services.AddServiceMantleConsul();
        // The lifecycle is registered once however often the capability is added, and registration
        // itself resolves no client factory, creates no client, and starts no timer or remote work:
        // the controller only takes ownership once the host starts it.
        Assert.Single(services, d => d.ServiceType.FullName == "Microsoft.Extensions.Hosting.IHostedService");
        Services = services.BuildServiceProvider();
        Assert.Equal(0, ClientFactory.Resolutions);
        Assert.Equal(0, ClientFactory.Calls);
    }

    internal ConsulClientProvider Provider => Services.GetRequiredService<ConsulClientProvider>();
    internal async Task ActivateAsync(Dictionary<string, string?> raw, long version = 1)
    {
        SnapshotSource.Read = new(LoaderService(), version, raw.Select(pair =>
        {
            Assert.True(Registry.TryGetDefinition(pair.Key, out var definition));
            var value = definition!.IsSensitive
                ? new SensitiveValueProtector(LoaderService(), definition.Key).Protect(pair.Value!, RootKey)
                : pair.Value!;
            return new PersistedServiceSettingValue(pair.Key, version, definition.ValueType, value);
        }));
        var result = await Loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
    }
    internal ServiceId SnapshotService { get; set; } = Service;
    private ServiceId LoaderService() => SnapshotService;

    internal static Dictionary<string, string?> Enabled(string token = Secret) => new()
    {
        [ConsulSettingDefinitions.Enabled] = "true",
        [ConsulSettingDefinitions.Endpoint] = "https://agent.example:8501",
        [ConsulSettingDefinitions.Token] = token,
        [ConsulSettingDefinitions.ServiceName] = "orders-api",
        [ConsulSettingDefinitions.Address] = "orders.example",
        [ConsulSettingDefinitions.Port] = "8080"
    };
    public void Dispose() { Services.Dispose(); Loader.Dispose(); }

    internal sealed class Factory : IConsulClientFactory
    {
        internal int Resolutions;
        internal int Calls;
        internal ConsulClientConfiguration? Configuration;
        internal Func<ConsulClientConfiguration, IConsulClient> CreateClient = _ => new StubClient();
        public IConsulClient Create(ConsulClientConfiguration configuration)
        {
            Interlocked.Increment(ref Calls);
            Configuration = configuration;
            return CreateClient(configuration);
        }
    }
    internal sealed class StubClient : IConsulClient
    {
        internal int Calls;
        internal bool Disposed;
        internal Func<CancellationToken, ValueTask<ConsulClientResult>> Operation = _ => ValueTask.FromResult(ConsulClientResult.Success);
        public ValueTask<ConsulClientResult> RegisterAsync(ConsulServiceRegistration registration, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Operation(cancellationToken); }
        public ValueTask<ConsulClientResult> DeregisterAsync(string id, CancellationToken cancellationToken = default)
        { Interlocked.Increment(ref Calls); return Operation(cancellationToken); }
        public void Dispose() => Disposed = true;
    }
    internal sealed class Definitions(ServiceSettingDefinition[] definitions) : IServiceSettingDefinitionProvider
    { public IEnumerable<ServiceSettingDefinition> GetDefinitions() => definitions; }
    internal sealed class Source : IServiceSettingSnapshotSource
    {
        internal ServiceSettingSnapshotRead? Read;
        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(ServiceId serviceId, CancellationToken cancellationToken = default) => ValueTask.FromResult(Read!);
    }
    private sealed class Root : IServiceSettingRootKeySource
    { public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default) => ValueTask.FromResult(RootKey); }
}
