using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Tests.Configuration;

public sealed class SettingSnapshotFingerprintTests
{
    private static readonly ServiceId Service = ServiceId.Parse("orders-api");

    // Same-version String pairs that the pre-fix Encoding.UTF8 replacement fallback folded onto
    // identical bytes. Each must now produce a Conflict instead of a silent reuse of the old
    // snapshot. Covers lone high, lone low, high-vs-real-replacement, reversed order, ASCII
    // sandwiching, and multiple lone surrogates.
    public static TheoryData<string, string> FoldedStringPairs => new()
    {
        { "\uD800", "\uD801" },
        { "\uD801", "\uD800" },
        { "\uD800", "\uFFFD" },
        { "\uFFFD", "\uD800" },
        { "\uDC00", "\uDC01" },
        { "\uDC01", "\uDC00" },
        { "x\uD800y", "x\uD801y" },
        { "x\uD800y", "x\uFFFDy" },
        { "\uD800\uD800", "\uD801\uD801" },
        { "\uDC00\uDC00", "\uDC01\uDC01" },
        { "a\uDC00b\uDBFFc", "a\uDC01b\uDBFFc" },
    };

    // Distinct well-formed-or-not boundary samples whose encodings must stay distinguishable.
    public static TheoryData<string, string> DistinctBoundaryPairs => new()
    {
        { "", "\u0000" },
        { "A", "\u4E2D" },
        { "\uD83D\uDE00", "\uFFFD" },
        { "AB", "A\u0000B" },
        { "\u0000", "\u0000\u0000" },
        { "\uFFFD\uFFFD", "\uFFFD" },
    };

    [Theory]
    [MemberData(nameof(FoldedStringPairs))]
    public async Task Same_version_folded_string_pair_conflicts_and_keeps_old_reference(
        string first, string second)
    {
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(1, StringValue(1, first)));
        using var loader = Loader(source, accessor);
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);
        var original = Current(accessor);

        source.Read = Read(1, StringValue(1, second));
        var conflict = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(conflict.Succeeded);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.Conflict,
            Assert.Single(conflict.Errors).ErrorCode);
        Assert.Null(conflict.Snapshot);
        Assert.Same(original, Current(accessor));
        Assert.Equal(first, Current(accessor).Values["product.name"].GetString());
        Assert.DoesNotContain(first, conflict.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(second, conflict.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(DistinctBoundaryPairs))]
    public async Task Same_version_distinct_boundary_pair_conflicts(string first, string second)
    {
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(1, StringValue(1, first)));
        using var loader = Loader(source, accessor);
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);
        var original = Current(accessor);

        source.Read = Read(1, StringValue(1, second));
        var conflict = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(conflict.Succeeded);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.Conflict,
            Assert.Single(conflict.Errors).ErrorCode);
        Assert.Same(original, Current(accessor));
    }

    [Fact]
    public async Task Identical_code_unit_sequence_rereads_without_activation()
    {
        const string value = "stable-\uD800-\uFFFD-value";
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(1, StringValue(1, value)));
        using var loader = Loader(source, accessor);
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);
        var original = Current(accessor);

        source.Read = Read(1, StringValue(1, value));
        var reread = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(reread.Succeeded);
        Assert.False(reread.Activated);
        Assert.Same(original, reread.Snapshot);
        Assert.Same(original, Current(accessor));
        Assert.Equal(value, Current(accessor).Values["product.name"].GetString());
    }

    [Fact]
    public async Task Higher_version_folded_string_activates_and_preserves_raw_value()
    {
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(1, StringValue(1, "\uD800")));
        using var loader = Loader(source, accessor);
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);

        source.Read = Read(2, StringValue(2, "\uD801"));
        var higher = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(higher.Succeeded);
        Assert.True(higher.Activated);
        Assert.Equal(2, higher.Snapshot!.Version);
        Assert.Equal("\uD801", higher.Snapshot.Values["product.name"].GetString());
        Assert.Same(higher.Snapshot, Current(accessor));
    }

    [Fact]
    public async Task Lower_version_folded_string_remains_stale()
    {
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(2, StringValue(2, "\uD800")));
        using var loader = Loader(source, accessor);
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);
        var original = Current(accessor);

        source.Read = Read(1, StringValue(1, "\uD801"));
        var stale = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(stale.Succeeded);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.Stale,
            Assert.Single(stale.Errors).ErrorCode);
        Assert.Same(original, Current(accessor));
    }

    [Fact]
    public async Task Field_boundaries_are_determined_by_the_length_prefix()
    {
        // ("ab","c") and ("a","bc") concatenate to the same "abc" run without a length prefix, so
        // the per-field code-unit-count prefix is what keeps the two snapshots distinct.
        var registry = Registry(
            new ServiceSettingDefinition("product.name", ServiceSettingValueType.String, isRequired: true),
            new ServiceSettingDefinition("product.label", ServiceSettingValueType.String, isRequired: true));
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(1,
            StringValue(1, "ab"),
            Value("product.label", 1, ServiceSettingValueType.String, "c")));
        using var loader = Loader(source, accessor, registry);
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);
        var original = Current(accessor);

        source.Read = Read(1,
            StringValue(1, "a"),
            Value("product.label", 1, ServiceSettingValueType.String, "bc"));
        var conflict = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(conflict.Succeeded);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.Conflict,
            Assert.Single(conflict.Errors).ErrorCode);
        Assert.Same(original, Current(accessor));
    }

    [Fact]
    public async Task Existing_normalization_equivalences_are_preserved()
    {
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(1,
            StringValue(1, "Orders"),
            Value("product.retries", 1, ServiceSettingValueType.Number, "2.50"),
            Value("product.enabled", 1, ServiceSettingValueType.Boolean, "TRUE"),
            Value("product.options", 1, ServiceSettingValueType.Json, "{ \"mode\": \"safe\" }")));
        using var loader = Loader(source, accessor, TypedRegistry());
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);
        var original = Current(accessor);

        source.Read = Read(1,
            StringValue(1, "Orders"),
            Value("product.retries", 1, ServiceSettingValueType.Number, "2.5"),
            Value("product.enabled", 1, ServiceSettingValueType.Boolean, "true"),
            Value("product.options", 1, ServiceSettingValueType.Json, "{\"mode\":\"safe\"}"));
        var reread = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.True(reread.Succeeded);
        Assert.False(reread.Activated);
        Assert.Same(original, reread.Snapshot);
        Assert.Same(original, Current(accessor));
    }

    [Fact]
    public async Task Optional_value_presence_participates_in_the_fingerprint()
    {
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(1,
            StringValue(1, "Orders"),
            Value("product.retries", 1, ServiceSettingValueType.Number, "5")));
        using var loader = Loader(source, accessor, TypedRegistry());
        Assert.True((await loader.RefreshAsync(TestContext.Current.CancellationToken)).Activated);
        var original = Current(accessor);

        source.Read = Read(1, StringValue(1, "Orders"));
        var conflict = await loader.RefreshAsync(TestContext.Current.CancellationToken);

        Assert.False(conflict.Succeeded);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.Conflict,
            Assert.Single(conflict.Errors).ErrorCode);
        Assert.Same(original, Current(accessor));
    }

    [Fact]
    public async Task Query_service_returns_empty_projection_on_fingerprint_conflict()
    {
        var registry = StringRegistry();
        var accessor = new ServiceSettingCurrentSnapshotAccessor();
        var source = new MutableSource(Read(1, StringValue(1, "\uD800")));
        using var loader = new ServiceSettingSnapshotLoader(Service, source, registry, accessor);
        var query = new ServiceSettingQueryService(registry, loader);

        var first = await query.GetCurrentAsync(TestContext.Current.CancellationToken);
        Assert.True(first.Succeeded);
        Assert.Equal(1, first.Version);

        source.Read = Read(1, StringValue(1, "\uD801"));
        var second = await query.GetCurrentAsync(TestContext.Current.CancellationToken);

        Assert.False(second.Succeeded);
        Assert.Null(second.Version);
        Assert.Empty(second.Values);
        Assert.Equal(
            WellKnownServiceSettingSnapshotErrorCodes.Conflict,
            Assert.Single(second.Errors).ErrorCode);
        Assert.DoesNotContain("\uD800", second.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("\uD801", second.ToString(), StringComparison.Ordinal);
    }

    private static ServiceSettingSnapshotLoader Loader(
        IServiceSettingSnapshotSource source,
        ServiceSettingCurrentSnapshotAccessor accessor,
        ServiceSettingDefinitionRegistry? registry = null) =>
        new(Service, source, registry ?? StringRegistry(), accessor);

    private static ServiceSettingDefinitionRegistry StringRegistry() =>
        Registry(new ServiceSettingDefinition("product.name", ServiceSettingValueType.String));

    private static ServiceSettingDefinitionRegistry TypedRegistry() =>
        Registry(
            new ServiceSettingDefinition("product.name", ServiceSettingValueType.String, isRequired: true),
            new ServiceSettingDefinition("product.retries", ServiceSettingValueType.Number),
            new ServiceSettingDefinition("product.enabled", ServiceSettingValueType.Boolean, defaultValue: "false"),
            new ServiceSettingDefinition("product.options", ServiceSettingValueType.Json));

    private static ServiceSettingDefinitionRegistry Registry(
        params ServiceSettingDefinition[] definitions) => new([new Definitions(definitions)]);

    private static ServiceSettingSnapshotRead Read(
        long version, params PersistedServiceSettingValue[] values) => new(Service, version, values);

    private static PersistedServiceSettingValue StringValue(long version, string value) =>
        new("product.name", version, ServiceSettingValueType.String, value);

    private static PersistedServiceSettingValue Value(
        string key, long version, ServiceSettingValueType type, string value) =>
        new(key, version, type, value);

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

    private sealed class MutableSource(ServiceSettingSnapshotRead read) : IServiceSettingSnapshotSource
    {
        public ServiceSettingSnapshotRead Read { get; set; } = read;

        public ValueTask<ServiceSettingSnapshotRead> LoadAsync(
            ServiceId serviceId, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Read);
    }
}
