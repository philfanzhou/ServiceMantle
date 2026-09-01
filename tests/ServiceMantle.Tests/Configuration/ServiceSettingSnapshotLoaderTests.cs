using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class ServiceSettingSnapshotLoaderTests
{
    private static readonly ServiceId Service = ServiceId.Parse("orders-api");
    private const string RootKey = "root-key-with-enough-entropy-for-tests";

    [Fact]
    public async Task Complete_snapshot_is_decrypted_typed_validated_and_atomically_activated()
    {
        const string secret = "smtp-password-secret";
        var source = new MutableSource(Read(7,
            Value("product.name", 7, ServiceSettingValueType.String, "Orders"),
            Value("product.retries", 7, ServiceSettingValueType.Number, "2.50"),
            Value("product.enabled", 7, ServiceSettingValueType.Boolean, "TRUE"),
            Value("product.options", 7, ServiceSettingValueType.Json, "{ \"mode\": \"safe\" }"),
            Value("product.token", 7, ServiceSettingValueType.String, Protect("product.token", secret))));
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = Loader(source, accessor, rootKeySource: new RootKeySource(RootKey));

        var result = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(result.Succeeded);
        Assert.True(result.Activated);
        Assert.Same(result.Snapshot, Current(accessor));
        Assert.Equal(7, result.Snapshot!.Version);
        Assert.Equal("Orders", result.Snapshot.Values["product.name"].GetString());
        Assert.Equal(2.50m, result.Snapshot.Values["product.retries"].GetNumber());
        Assert.True(result.Snapshot.Values["product.enabled"].GetBoolean());
        Assert.Equal("safe", result.Snapshot.Values["product.options"].GetJson().GetProperty("mode").GetString());
        Assert.Equal(secret, result.Snapshot.Values["product.token"].GetString());
        Assert.Throws<NotSupportedException>(() =>
            ((IDictionary<string, ServiceSettingValue>)result.Snapshot.Values).Add(
                "product.other",
                result.Snapshot.Values["product.name"]));
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, result.Snapshot.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task First_failure_leaves_current_explicitly_unavailable()
    {
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = Loader(new MutableSource(Read(1)), accessor);

        var result = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.MissingRequired,
            Assert.Single(result.Errors).ErrorCode);
        Assert.False(accessor.TryGetCurrent(out var current));
        Assert.Null(current);
    }

    [Theory]
    [InlineData("mixed", WellKnownServiceSettingSnapshotErrorCodes.MixedVersion)]
    [InlineData("duplicate", WellKnownServiceSettingSnapshotErrorCodes.DuplicateKey)]
    [InlineData("unknown", WellKnownServiceSettingSnapshotErrorCodes.UnknownKey)]
    [InlineData("type", WellKnownServiceSettingSnapshotErrorCodes.ValueTypeMismatch)]
    [InlineData("conversion", WellKnownServiceSettingSnapshotErrorCodes.ValueTypeMismatch)]
    public async Task Structural_and_type_failures_are_closed(string scenario, string expectedCode)
    {
        var values = scenario switch
        {
            "mixed" => new[] { Value("product.name", 2, ServiceSettingValueType.String, "Orders") },
            "duplicate" =>
            [
                Value("product.name", 1, ServiceSettingValueType.String, "Orders"),
                Value("PRODUCT.NAME", 1, ServiceSettingValueType.String, "Other"),
            ],
            "unknown" => new[] { Value("password=untrusted", 1, ServiceSettingValueType.String, "secret") },
            "type" => new[] { Value("product.name", 1, ServiceSettingValueType.Number, "1") },
            "conversion" =>
            [
                Value("product.name", 1, ServiceSettingValueType.String, "Orders"),
                Value("product.retries", 1, ServiceSettingValueType.Number, "not-a-number-secret"),
            ],
            _ => throw new InvalidOperationException(),
        };
        using var loader = Loader(new MutableSource(Read(1, values)));

        var result = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Contains(result.Errors, error => error.ErrorCode == expectedCode);
        Assert.DoesNotContain("untrusted", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("not-a-number-secret", result.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("plaintext", WellKnownServiceSettingSnapshotErrorCodes.SensitiveEnvelopeRequired)]
    [InlineData("version", WellKnownServiceSettingSnapshotErrorCodes.SensitiveVersionUnsupported)]
    [InlineData("malformed", WellKnownServiceSettingSnapshotErrorCodes.SensitiveCiphertextInvalid)]
    [InlineData("authentication", WellKnownServiceSettingSnapshotErrorCodes.SensitiveAuthenticationFailed)]
    [InlineData("purpose", WellKnownServiceSettingSnapshotErrorCodes.SensitiveAuthenticationFailed)]
    [InlineData("key", WellKnownServiceSettingSnapshotErrorCodes.SensitiveKeyUnavailable)]
    public async Task Sensitive_failures_are_classified_without_secret_material(
        string scenario,
        string expectedCode)
    {
        const string secret = "sensitive-payload-never-diagnosed";
        var persisted = scenario switch
        {
            "plaintext" => secret,
            "version" => "sm:v2:" + secret,
            "malformed" => "sm:v1:not-base64-" + secret,
            "authentication" => Protect("product.token", secret, "different-root-key"),
            "purpose" => Protect("different.purpose", secret),
            "key" => Protect("product.token", secret),
            _ => throw new InvalidOperationException(),
        };
        var rootSource = scenario == "key" ? null : new RootKeySource(RootKey);
        using var loader = Loader(
            new MutableSource(Read(1,
                Value("product.name", 1, ServiceSettingValueType.String, "Orders"),
                Value("product.token", 1, ServiceSettingValueType.String, persisted))),
            rootKeySource: rootSource);

        var result = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(expectedCode, Assert.Single(result.Errors).ErrorCode);
        Assert.DoesNotContain(secret, result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(persisted, result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Constraint_failure_does_not_publish_partial_values()
    {
        var registry = Registry(
            new ServiceSettingDefinition("product.name", ServiceSettingValueType.String, isRequired: true),
            new ServiceSettingDefinition(
                "product.retries",
                ServiceSettingValueType.Number,
                constraints: [new NumberRangeSettingConstraint(1, 5)]));
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = Loader(
            new MutableSource(Read(1,
                Value("product.name", 1, ServiceSettingValueType.String, "Orders"),
                Value("product.retries", 1, ServiceSettingValueType.Number, "9"))),
            accessor,
            registry);

        var result = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(result.Succeeded);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.ValidationFailed,
            Assert.Single(result.Errors).ErrorCode);
        Assert.False(accessor.TryGetCurrent(out _));
    }

    [Fact]
    public async Task Version_rules_preserve_reference_and_compare_normalized_content()
    {
        var source = new MutableSource(Read(2,
            Value("product.name", 2, ServiceSettingValueType.String, "Orders"),
            Value("product.retries", 2, ServiceSettingValueType.Number, "2.50")));
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = Loader(source, accessor);
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);
        var original = Current(accessor);

        source.Read = Read(2,
            Value("product.name", 2, ServiceSettingValueType.String, "Orders"),
            Value("product.retries", 2, ServiceSettingValueType.Number, "2.5"));
        var idempotent = await loader.RefreshAsync(TestContext.Current.CancellationToken);
        source.Read = Read(2,
            Value("product.name", 2, ServiceSettingValueType.String, "Different"));
        var conflict = await loader.RefreshAsync(TestContext.Current.CancellationToken);
        source.Read = Read(1,
            Value("product.name", 1, ServiceSettingValueType.String, "Older"));
        var stale = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(idempotent.Succeeded);
        Assert.False(idempotent.Activated);
        Assert.Same(original, idempotent.Snapshot);
        Assert.Equal(WellKnownServiceSettingSnapshotErrorCodes.Conflict, Assert.Single(conflict.Errors).ErrorCode);
        Assert.Equal(WellKnownServiceSettingSnapshotErrorCodes.Stale, Assert.Single(stale.Errors).ErrorCode);
        Assert.Same(original, Current(accessor));
    }

    [Fact]
    public async Task Source_exception_and_caller_cancellation_preserve_existing_reference()
    {
        var source = new MutableSource(Read(1,
            Value("product.name", 1, ServiceSettingValueType.String, "Orders")));
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = Loader(source, accessor);
        await loader.RefreshAsync(TestContext.Current.CancellationToken);
        var original = Current(accessor);
        source.Failure = new InvalidOperationException("Password=provider-secret");

        var failed = await loader.RefreshAsync(TestContext.Current.CancellationToken);
        source.Failure = new OperationCanceledException("internal cancellation secret");
        var internalCancellation = await loader.RefreshAsync(TestContext.Current.CancellationToken);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            loader.RefreshAsync(cancellation.Token).AsTask());

        Assert.Equal(WellKnownServiceSettingSnapshotErrorCodes.LoadFailed, Assert.Single(failed.Errors).ErrorCode);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.LoadFailed,
            Assert.Single(internalCancellation.Errors).ErrorCode);
        Assert.DoesNotContain("provider-secret", failed.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("internal cancellation secret", internalCancellation.ToString(), StringComparison.Ordinal);
        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.Same(original, Current(accessor));
    }

    [Fact]
    public async Task Cancellation_after_source_read_does_not_activate_materialized_values()
    {
        using var cancellation = new CancellationTokenSource();
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = Loader(new CancellingSource(cancellation), accessor);

        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            loader.RefreshAsync(cancellation.Token).AsTask());

        Assert.Equal(cancellation.Token, exception.CancellationToken);
        Assert.False(accessor.TryGetCurrent(out _));
    }

    [Fact]
    public async Task Concurrent_refreshes_are_serialized_and_readers_observe_only_complete_snapshots()
    {
        var source = new BlockingSource();
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = Loader(source, accessor);
        var first = loader.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        await source.FirstEntered.Task.WaitAsync(TestContext.Current.CancellationToken);
        var second = loader.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        await Task.Delay(50, TestContext.Current.CancellationToken);
        Assert.Equal(1, source.CallCount);
        Assert.False(accessor.TryGetCurrent(out _));

        source.ReleaseFirst.SetResult();
        await first;
        await second;

        Assert.Equal(2, source.CallCount);
        Assert.Equal(1, source.MaximumConcurrentCalls);
        Assert.Equal(2, Current(accessor).Version);
        Assert.Equal("second", Current(accessor).Values["product.name"].GetString());
    }

    [Fact]
    public async Task Concurrent_readers_observe_the_complete_old_or_complete_new_object()
    {
        var source = new MutableSource(Read(1,
            Value("product.name", 1, ServiceSettingValueType.String, "old"),
            Value("product.retries", 1, ServiceSettingValueType.Number, "1")));
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        using var loader = Loader(source, accessor);
        await loader.RefreshAsync(TestContext.Current.CancellationToken);
        source.Read = Read(2,
            Value("product.name", 2, ServiceSettingValueType.String, "new"),
            Value("product.retries", 2, ServiceSettingValueType.Number, "2"));

        var refresh = loader.RefreshAsync(TestContext.Current.CancellationToken).AsTask();
        var observations = Enumerable.Range(0, 1_000).Select(_ => Task.Run(() =>
        {
            var snapshot = Current(accessor);
            var name = snapshot.Values["product.name"].GetString();
            var retries = snapshot.Values["product.retries"].GetNumber();
            Assert.True(
                snapshot.Version == 1 && name == "old" && retries == 1 ||
                snapshot.Version == 2 && name == "new" && retries == 2);
        }, TestContext.Current.CancellationToken));

        await Task.WhenAll(observations.Append(refresh));
        Assert.Equal(2, Current(accessor).Version);
    }

    [Fact]
    public void Registration_resolves_one_shared_accessor_and_loader()
    {
        var services = new ServiceCollection();
        services.AddSingleton(Service);
        services.AddSingleton<IServiceSettingStore>(new Store(Read(1,
            Value("product.name", 1, ServiceSettingValueType.String, "Orders"))));
        services.AddSingleton<IServiceSettingDefinitionProvider>(new Definitions(DefaultDefinitions()));
        services.AddServiceMantleSettingSnapshots();
        using var provider = services.BuildServiceProvider();

        Assert.Same(
            provider.GetRequiredService<ServiceSettingCurrentSnapshotAccessor>(),
            provider.GetRequiredService<IServiceSettingCurrentSnapshotAccessor>());
        Assert.Same(
            provider.GetRequiredService<ServiceSettingSnapshotLoader>(),
            provider.GetRequiredService<ServiceSettingSnapshotLoader>());
    }

    private static ServiceSettingSnapshotLoader Loader(
        IServiceSettingSnapshotSource source,
        ServiceSettingCurrentSnapshotAccessor? accessor = null,
        ServiceSettingDefinitionRegistry? registry = null,
        IServiceSettingRootKeySource? rootKeySource = null) =>
        new(Service, source, registry ?? Registry(DefaultDefinitions()),
            accessor ?? new ServiceSettingCurrentSnapshotAccessor(), rootKeySource);

    private static ServiceSettingDefinition[] DefaultDefinitions() =>
    [
        new("product.name", ServiceSettingValueType.String, isRequired: true),
        new("product.retries", ServiceSettingValueType.Number),
        new("product.enabled", ServiceSettingValueType.Boolean, defaultValue: "false"),
        new("product.options", ServiceSettingValueType.Json),
        new("product.token", ServiceSettingValueType.String, isSensitive: true),
    ];

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

    private static ServiceSettingSnapshot Current(ServiceSettingCurrentSnapshotAccessor accessor)
    {
        Assert.True(accessor.TryGetCurrent(out var current));
        return current!;
    }

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

    private sealed class MutableSource(ServiceSettingSnapshotRead read) : IServiceSettingSnapshotSource
    {
        public ServiceSettingSnapshotRead Read { get; set; } = read;
        public Exception? Failure { get; set; }

        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default) =>
            Failure is null
                ? ValueTask.FromResult(Read)
                : ValueTask.FromException<ServiceSettingSnapshotRead>(Failure);
    }

    private sealed class BlockingSource : IServiceSettingSnapshotSource
    {
        private int activeCalls;
        public TaskCompletionSource FirstEntered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource ReleaseFirst { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
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
                        call == 1 ? "first" : "second"));
            }
            finally
            {
                Interlocked.Decrement(ref activeCalls);
            }
        }
    }

    private sealed class CancellingSource(CancellationTokenSource cancellation)
        : IServiceSettingSnapshotSource
    {
        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId,
            CancellationToken cancellationToken = default)
        {
            var read = Read(1,
                Value("product.name", 1, ServiceSettingValueType.String, "cancelled-secret"));
            cancellation.Cancel();
            return ValueTask.FromResult(read);
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
