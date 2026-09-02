using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class ServiceSettingQueryServiceTests
{
    private static readonly ServiceId Service = ServiceId.Parse("orders-api");
    private const string RootKey = "root-key-with-enough-entropy-for-query-tests";

    [Fact]
    public void Definitions_are_safe_immutable_and_stably_sorted()
    {
        const string defaultValue = "default-material-never-projected";
        var registry = Registry(
            new ServiceSettingDefinition(
                "product.zeta",
                ServiceSettingValueType.String,
                isRequired: true,
                defaultValue: defaultValue,
                requiresRestart: true,
                constraints: [new StringLengthSettingConstraint(1, 64)]),
            new ServiceSettingDefinition(
                "PRODUCT.alpha",
                ServiceSettingValueType.Boolean,
                isSensitive: true));
        using var loader = Loader(new Source(Read(0)), registry);
        var service = new ServiceSettingQueryService(registry, loader);

        var definitions = service.GetDefinitions();

        Assert.Equal(["product.alpha", "product.zeta"], definitions.Select(item => item.Key));
        Assert.Equal(
            [
                nameof(ServiceSettingDefinitionProjection.HasDefault),
                nameof(ServiceSettingDefinitionProjection.IsRequired),
                nameof(ServiceSettingDefinitionProjection.IsSensitive),
                nameof(ServiceSettingDefinitionProjection.Key),
                nameof(ServiceSettingDefinitionProjection.RequiresRestart),
                nameof(ServiceSettingDefinitionProjection.ValueType),
            ],
            typeof(ServiceSettingDefinitionProjection).GetProperties()
                .Select(property => property.Name)
                .Order(StringComparer.Ordinal));
        Assert.True(definitions[1].IsRequired);
        Assert.True(definitions[1].HasDefault);
        Assert.True(definitions[1].RequiresRestart);
        Assert.Empty(typeof(ServiceSettingDefinitionProjection).GetConstructors());
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ServiceSettingDefinitionProjection>)definitions).Add(definitions[0]));
        Assert.DoesNotContain(defaultValue, definitions[1].ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Current_values_use_one_version_and_fixed_safe_normalization()
    {
        const string stringSecret = "password=query-string-secret";
        const string jsonSecret = "query-json-secret";
        var registry = Registry(
            new ServiceSettingDefinition("product.string", ServiceSettingValueType.String),
            new ServiceSettingDefinition("product.number", ServiceSettingValueType.Number),
            new ServiceSettingDefinition("product.boolean", ServiceSettingValueType.Boolean),
            new ServiceSettingDefinition("product.json", ServiceSettingValueType.Json),
            new ServiceSettingDefinition("product.default", ServiceSettingValueType.Number, defaultValue: "3.00"),
            new ServiceSettingDefinition("product.missing", ServiceSettingValueType.String),
            new ServiceSettingDefinition("product.secret-string", ServiceSettingValueType.String, isSensitive: true),
            new ServiceSettingDefinition("product.secret-json", ServiceSettingValueType.Json, isSensitive: true));
        var source = new Source(Read(7,
            Value("product.string", 7, ServiceSettingValueType.String, "Orders"),
            Value("product.number", 7, ServiceSettingValueType.Number, "2.500"),
            Value("product.boolean", 7, ServiceSettingValueType.Boolean, "TRUE"),
            Value("product.json", 7, ServiceSettingValueType.Json, "{ \"mode\" : true }"),
            Value("product.secret-string", 7, ServiceSettingValueType.String,
                Protect("product.secret-string", stringSecret)),
            Value("product.secret-json", 7, ServiceSettingValueType.Json,
                Protect("product.secret-json", $"{{\"token\":\"{jsonSecret}\"}}"))));
        using var loader = Loader(source, registry, new RootKeySource(RootKey));
        var service = new ServiceSettingQueryService(registry, loader);

        var result = await service.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.Equal(7, result.Version);
        Assert.Equal(1, source.CallCount);
        Assert.Equal("Orders", Current(result, "product.string").Value);
        Assert.Equal("2.5", Current(result, "product.number").Value);
        Assert.Equal("true", Current(result, "product.boolean").Value);
        Assert.Equal("{\"mode\":true}", Current(result, "product.json").Value);
        Assert.Equal(ServiceSettingValueSource.Default, Current(result, "product.default").Source);
        Assert.Equal("3", Current(result, "product.default").Value);
        Assert.Equal(ServiceSettingValueSource.Missing, Current(result, "product.missing").Source);
        Assert.False(Current(result, "product.missing").HasValue);
        Assert.Null(Current(result, "product.missing").Value);
        AssertSensitive(result, "product.secret-string");
        AssertSensitive(result, "product.secret-json");
        Assert.DoesNotContain(stringSecret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(jsonSecret, result.ToString(), StringComparison.Ordinal);
        Assert.All(result.Values, item =>
        {
            Assert.DoesNotContain(stringSecret, item.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(jsonSecret, item.ToString(), StringComparison.Ordinal);
        });
        Assert.Throws<NotSupportedException>(() =>
            ((IList<ServiceSettingCurrentValueProjection>)result.Values).Add(result.Values[0]));
    }

    [Theory]
    [InlineData("unknown", WellKnownServiceSettingSnapshotErrorCodes.UnknownKey)]
    [InlineData("mixed", WellKnownServiceSettingSnapshotErrorCodes.MixedVersion)]
    [InlineData("type", WellKnownServiceSettingSnapshotErrorCodes.ValueTypeMismatch)]
    [InlineData("decrypt", WellKnownServiceSettingSnapshotErrorCodes.SensitiveAuthenticationFailed)]
    [InlineData("key", WellKnownServiceSettingSnapshotErrorCodes.SensitiveKeyUnavailable)]
    [InlineData("storage", WellKnownServiceSettingSnapshotErrorCodes.LoadFailed)]
    public async Task Refresh_failures_are_projected_without_partial_or_old_values(
        string scenario,
        string expectedCode)
    {
        var registry = Registry(
            new ServiceSettingDefinition("product.name", ServiceSettingValueType.String, isRequired: true),
            new ServiceSettingDefinition("product.secret", ServiceSettingValueType.String, isSensitive: true));
        var source = new Source(Read(1,
            Value("product.name", 1, ServiceSettingValueType.String, "old")));
        using var loader = Loader(
            source,
            registry,
            scenario == "key" ? null : new RootKeySource(RootKey));
        var service = new ServiceSettingQueryService(registry, loader);
        Assert.True((await service.GetCurrentAsync(TestContext.Current.CancellationToken)).Succeeded);

        source.Failure = scenario == "storage"
            ? new InvalidOperationException("Host=provider-secret")
            : null;
        source.Read = scenario switch
        {
            "unknown" => Read(2, Value("unknown.key", 2, ServiceSettingValueType.String, "secret")),
            "mixed" => Read(2, Value("product.name", 1, ServiceSettingValueType.String, "secret")),
            "type" => Read(2, Value("product.name", 2, ServiceSettingValueType.Number, "42")),
            "decrypt" => Read(2,
                Value("product.name", 2, ServiceSettingValueType.String, "new"),
                Value("product.secret", 2, ServiceSettingValueType.String,
                    Protect("product.secret", "secret", "different-root-key"))),
            "key" => Read(2,
                Value("product.name", 2, ServiceSettingValueType.String, "new"),
                Value("product.secret", 2, ServiceSettingValueType.String,
                    Protect("product.secret", "secret"))),
            "storage" => source.Read,
            _ => throw new InvalidOperationException(),
        };
        var result = await service.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Null(result.Version);
        Assert.Empty(result.Values);
        Assert.Equal(expectedCode, Assert.Single(result.Errors).ErrorCode);
        Assert.DoesNotContain("provider-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Equal_version_conflict_is_not_returned_as_a_successful_old_projection()
    {
        var registry = Registry(new ServiceSettingDefinition(
            "product.name", ServiceSettingValueType.String, isRequired: true));
        var source = new Source(Read(4,
            Value("product.name", 4, ServiceSettingValueType.String, "old")));
        using var loader = Loader(source, registry);
        var service = new ServiceSettingQueryService(registry, loader);
        Assert.True((await service.GetCurrentAsync(TestContext.Current.CancellationToken)).Succeeded);
        source.Read = Read(4,
            Value("product.name", 4, ServiceSettingValueType.String, "new"));

        var result = await service.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.Conflict,
            Assert.Single(result.Errors).ErrorCode);
        Assert.Null(result.Version);
        Assert.Empty(result.Values);
    }

    [Fact]
    public async Task Caller_cancellation_propagates_the_original_token()
    {
        var registry = Registry();
        using var loader = Loader(new Source(Read(0)), registry);
        var service = new ServiceSettingQueryService(registry, loader);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.GetCurrentAsync(cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
    }

    [Fact]
    public async Task Concurrent_queries_never_mix_fields_from_two_versions()
    {
        var registry = Registry(
            new ServiceSettingDefinition("product.name", ServiceSettingValueType.String),
            new ServiceSettingDefinition("product.number", ServiceSettingValueType.Number));
        var source = new BlockingSource();
        using var loader = Loader(source, registry);
        var service = new ServiceSettingQueryService(registry, loader);

        var first = service.GetCurrentAsync(TestContext.Current.CancellationToken).AsTask();
        await source.FirstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = service.GetCurrentAsync(TestContext.Current.CancellationToken).AsTask();
        source.ReleaseFirst.SetResult();
        var results = await Task.WhenAll(first, second);

        Assert.Equal(2, source.CallCount);
        Assert.Equal(1, source.MaximumConcurrentCalls);
        Assert.Contains(results, result =>
            result.Version == 1 &&
            Current(result, "product.name").Value == "first" &&
            Current(result, "product.number").Value == "1");
        Assert.Contains(results, result =>
            result.Version == 2 &&
            Current(result, "product.name").Value == "second" &&
            Current(result, "product.number").Value == "2");
    }

    [Fact]
    public void Registration_is_idempotent_and_resolves_one_query_service()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Service);
        services.AddSingleton<IServiceSettingStore>(new Store(Read(0)));

        services.AddServiceMantleSettingSnapshots();
        services.AddServiceMantleSettingSnapshots();
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<ServiceSettingQueryService>(),
            provider.GetRequiredService<ServiceSettingQueryService>());
        Assert.Single(provider.GetServices<ServiceSettingQueryService>());
    }

    private static void AssertSensitive(ServiceSettingCurrentQueryResult result, string key)
    {
        var value = Current(result, key);
        Assert.True(value.IsSensitive);
        Assert.True(value.HasValue);
        Assert.Equal(ServiceSettingValueSource.Persisted, value.Source);
        Assert.Null(value.Value);
    }

    private static ServiceSettingCurrentValueProjection Current(
        ServiceSettingCurrentQueryResult result,
        string key) => Assert.Single(result.Values, value => value.Key == key);

    private static ServiceSettingSnapshotLoader Loader(
        IServiceSettingSnapshotSource source,
        ServiceSettingDefinitionRegistry registry,
        IServiceSettingRootKeySource? rootKeySource = null) =>
        new(Service, source, registry, new ServiceSettingCurrentSnapshotAccessor(), rootKeySource);

    private static ServiceSettingDefinitionRegistry Registry(
        params ServiceSettingDefinition[] definitions) => new([new Definitions(definitions)]);

    private static ServiceSettingSnapshotRead Read(
        long version,
        params PersistedServiceSettingValue[] values) => new(Service, version, values);

    private static PersistedServiceSettingValue Value(
        string key,
        long version,
        ServiceSettingValueType type,
        string value) => new(key, version, type, value);

    private static string Protect(string purpose, string value, string rootKey = RootKey) =>
        new SensitiveValueProtector(Service, purpose).Protect(value, rootKey);

    private sealed class Definitions(params ServiceSettingDefinition[] definitions)
        : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() => definitions;
    }

    private sealed class RootKeySource(string rootKey) : IServiceSettingRootKeySource
    {
        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(rootKey);
    }

    private sealed class Source(ServiceSettingSnapshotRead read) : IServiceSettingSnapshotSource
    {
        public ServiceSettingSnapshotRead Read { get; set; } = read;
        public Exception? Failure { get; set; }
        public int CallCount { get; private set; }

        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            return Failure is null
                ? ValueTask.FromResult(Read)
                : ValueTask.FromException<ServiceSettingSnapshotRead>(Failure);
        }
    }

    private sealed class BlockingSource : IServiceSettingSnapshotSource
    {
        private int activeCalls;
        public TaskCompletionSource FirstEntered { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int CallCount { get; private set; }
        public int MaximumConcurrentCalls { get; private set; }

        public async ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            var active = Interlocked.Increment(ref activeCalls);
            MaximumConcurrentCalls = Math.Max(MaximumConcurrentCalls, active);
            var call = ++CallCount;
            try
            {
                if (call == 1)
                {
                    FirstEntered.SetResult();
                    await ReleaseFirst.Task.WaitAsync(cancellationToken);
                }

                return Read(call,
                    Value("product.name", call, ServiceSettingValueType.String,
                        call == 1 ? "first" : "second"),
                    Value("product.number", call, ServiceSettingValueType.Number, call.ToString()));
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }
    }

    private sealed class Store(ServiceSettingSnapshotRead read) : IServiceSettingStore
    {
        public ValueTask<ServiceSettingStoreSnapshot> LoadAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ServiceSettingStoreSnapshot(
                serviceId,
                read.Version,
                read.Values.ToDictionary(value => value.Key, value => value.Value),
                DateTimeOffset.UtcNow,
                "test",
                restartRequired: false));

        public ValueTask<ServiceSettingStoreUpdateResult> UpdateAsync(
            ServiceId serviceId,
            ServiceSettingStoreUpdate update,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }
}
