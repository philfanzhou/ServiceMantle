using System.Text.Json;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using ServiceMantle.Tests.Migration;
using Xunit;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Covers the shared provider-id invariant: one resolver snapshot decides the canonical id used
/// by persistence, provider dispatch, target preparation lookup, and migration lock lookup.
/// </summary>
public sealed class ProviderIdCanonicalizationTests
{
    private const string CanonicalId = "PostgreSQL";
    private const string AliasId = "postgres";

    [Fact]
    public void Create_writes_the_canonical_id_for_a_registered_alias()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, CreateProviderRegistry());

        store.Create(CreateConfiguration(AliasId));

        Assert.Equal(CanonicalId, ReadPersistedProvider(store));
    }

    [Fact]
    public void Replace_writes_the_canonical_id_for_a_registered_alias()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, CreateProviderRegistry());

        store.Create(CreateConfiguration(CanonicalId));
        store.Replace(CreateConfiguration(AliasId, "Host=replaced;Password=p"));

        Assert.Equal(CanonicalId, ReadPersistedProvider(store));
    }

    [Fact]
    public void Alias_casing_variants_all_land_on_the_descriptor_casing()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, CreateProviderRegistry());

        store.Create(CreateConfiguration("POSTGRES"));

        Assert.Equal(CanonicalId, ReadPersistedProvider(store));
        Assert.Equal(CanonicalId, store.Load().Database.Provider);
    }

    [Fact]
    public void Unregistered_but_valid_provider_round_trips_unchanged()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, CreateProviderRegistry());

        store.Create(CreateConfiguration("Vendor.Custom-Db_1"));

        Assert.Equal("Vendor.Custom-Db_1", ReadPersistedProvider(store));
        Assert.Equal("Vendor.Custom-Db_1", store.Load().Database.Provider);
    }

    [Fact]
    public void Load_canonicalizes_an_existing_alias_without_rewriting_the_file()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, CreateProviderRegistry());
        var contents = $$"""
            {
              "FormatVersion": 1,
              "ServiceId": "signacore",
              "Database": {
                "Provider": "{{AliasId}}",
                "ServerVersion": "15",
                "ConnectionString": "Host=db;Password=legacy"
              },
              "MasterKey": "legacy-master-key"
            }
            """;
        File.WriteAllText(store.FilePath, contents);
        var writeTime = File.GetLastWriteTimeUtc(store.FilePath);

        var loaded = store.Load();
        var tryLoaded = store.TryLoad();

        Assert.Equal(CanonicalId, loaded.Database.Provider);
        Assert.Equal(CanonicalId, tryLoaded!.Database.Provider);

        // Reading must not rewrite the file; the alias stays on disk until the next Replace().
        Assert.Equal(contents, File.ReadAllText(store.FilePath));
        Assert.Equal(writeTime, File.GetLastWriteTimeUtc(store.FilePath));
        Assert.Equal(AliasId, ReadPersistedProvider(store));

        var manager = new BootstrapConfigurationManager(
            store,
            InstanceId.Parse("Node-A3"),
            new BootstrapDatabaseCandidateValidator(
                new BootstrapDatabaseProviderRegistry(
                    [new FakeBootstrapProvider(
                        DatabaseProviderIdResolverTests.Descriptor(CanonicalId, AliasId))])));

        Assert.Equal(CanonicalId, manager.GetStatus().Provider);
        Assert.Equal(AliasId, ReadPersistedProvider(store));
    }

    [Fact]
    public void Unregistered_provider_is_not_mapped_onto_another_capability()
    {
        var resolver = CreateResolver();
        var preparationRegistry = new DatabaseTargetPreparationProviderRegistry(
            [new FakePreparationProvider(CanonicalId)],
            resolver);
        var lockRegistry = new DatabaseMigrationLockProviderRegistry(
            [new FakeMigrationLockProvider(CanonicalId)],
            resolver);

        Assert.False(preparationRegistry.TryGetProvider("Vendor.Custom-Db_1", out var preparationProvider));
        Assert.Null(preparationProvider);
        Assert.False(lockRegistry.TryGetProvider("Vendor.Custom-Db_1", out var lockProvider));
        Assert.Null(lockProvider);
    }

    [Fact]
    public void Load_preserves_an_unregistered_provider_id_from_an_existing_file()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, CreateProviderRegistry());
        File.WriteAllText(store.FilePath, """
            {
              "FormatVersion": 1,
              "ServiceId": "signacore",
              "Database": {
                "Provider": "  Vendor.Custom-Db_1  ",
                "ConnectionString": "Host=db;Password=legacy"
              },
              "MasterKey": "legacy-master-key"
            }
            """);

        Assert.Equal("Vendor.Custom-Db_1", store.Load().Database.Provider);
    }

    [Fact]
    public void Load_rejects_a_syntactically_invalid_provider_id()
    {
        using var directory = TemporaryDirectory.Create();
        var store = CreateStore(directory, CreateProviderRegistry());
        File.WriteAllText(store.FilePath, """
            {
              "Database": {
                "Provider": "has space",
                "ConnectionString": "Host=db;Password=legacy"
              },
              "MasterKey": "legacy-master-key"
            }
            """);

        Assert.Throws<BootstrapException>(() => store.Load());
    }

    [Fact]
    public void Store_requires_a_provider_registry()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new BootstrapFileStore(ServiceId.Parse("signacore"), null!));
        Assert.Throws<ArgumentNullException>(() =>
            new BootstrapFileStore("signacore", null!));
    }

    [Fact]
    public void Store_canonicalizes_through_the_registry_snapshot_instance()
    {
        using var directory = TemporaryDirectory.Create();
        var providerRegistry = CreateProviderRegistry();
        var store = CreateStore(directory, providerRegistry);

        // The store must not build a second snapshot from the same descriptors: the guarantee is
        // that it resolves through the very table the candidate dispatch uses.
        Assert.Same(providerRegistry, store.ProviderRegistry);
        Assert.Same(providerRegistry.ProviderIdResolver, store.ProviderIdResolver);
    }

    [Theory]
    [InlineData(typeof(BootstrapFileStore), typeof(BootstrapDatabaseProviderRegistry))]
    [InlineData(typeof(DatabaseTargetPreparationProviderRegistry), typeof(DatabaseProviderIdResolver))]
    [InlineData(typeof(DatabaseMigrationLockProviderRegistry), typeof(DatabaseProviderIdResolver))]
    public void Type_has_no_constructor_that_skips_the_shared_snapshot(Type type, Type snapshotType)
    {
        // Guards the invariant that no public construction path can accept an alias it cannot
        // resolve. Every public constructor must take the shared snapshot, and it must be required:
        // an optional parameter that defaults to an empty snapshot is the same bypass with a
        // different spelling, because the caller silently gets a snapshot other than the one that
        // persisted the provider id. The store is bound to the registry itself, so it cannot be
        // handed a snapshot that disagrees with the registry that dispatches candidate validation.
        var constructors = type.GetConstructors();

        Assert.NotEmpty(constructors);
        Assert.All(
            constructors,
            constructor => Assert.Contains(
                constructor.GetParameters(),
                parameter => parameter.ParameterType == snapshotType && !parameter.IsOptional));
    }

    [Fact]
    public void Capability_registries_reject_a_null_resolver()
    {
        Assert.Throws<ArgumentNullException>(() =>
            new DatabaseTargetPreparationProviderRegistry([], null!));
        Assert.Throws<ArgumentNullException>(() =>
            new DatabaseMigrationLockProviderRegistry([], null!));
    }

    [Fact]
    public async Task Candidate_validator_dispatches_the_canonical_provider_id()
    {
        var provider = new FakeBootstrapProvider(
            DatabaseProviderIdResolverTests.Descriptor(CanonicalId, AliasId));
        var registry = new BootstrapDatabaseProviderRegistry([provider]);
        var validator = new BootstrapDatabaseCandidateValidator(registry);
        var candidate = new BootstrapConfiguration(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(AliasId, "16", "Host=db;Password=p"),
            "validator-master-key");

        var result = await validator.ValidateAsync(candidate, CancellationToken.None);

        Assert.True(result.IsValid);
        Assert.Equal(CanonicalId, provider.LastValidated!.Provider);
        Assert.Equal("16", provider.LastValidated.ServerVersion);
        Assert.Equal("Host=db;Password=p", provider.LastValidated.ConnectionString);
    }

    [Fact]
    public void Preparation_registry_resolves_an_alias_to_the_registered_capability()
    {
        var registry = new DatabaseTargetPreparationProviderRegistry(
            [new FakePreparationProvider(CanonicalId)],
            CreateResolver());

        Assert.True(registry.TryGetProvider(AliasId, out var byAlias));
        Assert.True(registry.TryGetProvider(CanonicalId, out var byCanonicalId));
        Assert.Same(byAlias, byCanonicalId);
    }

    [Fact]
    public void Preparation_registry_registered_under_an_alias_is_found_by_the_canonical_id()
    {
        var registry = new DatabaseTargetPreparationProviderRegistry(
            [new FakePreparationProvider(AliasId)],
            CreateResolver());

        Assert.True(registry.TryGetProvider(CanonicalId, out var provider));
        Assert.NotNull(provider);
    }

    [Fact]
    public void Preparation_registry_rejects_a_canonical_and_alias_duplicate()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseTargetPreparationProviderRegistry(
            [new FakePreparationProvider(CanonicalId), new FakePreparationProvider(AliasId)],
            CreateResolver()));
    }

    [Fact]
    public void Preparation_registry_fails_closed_when_the_capability_is_not_registered()
    {
        // Alias resolution must never imply a capability: the bootstrap provider exists, the
        // preparation provider does not.
        var registry = new DatabaseTargetPreparationProviderRegistry([], CreateResolver());

        Assert.False(registry.TryGetProvider(AliasId, out var provider));
        Assert.Null(provider);
        Assert.False(registry.TryGetProvider(CanonicalId, out _));
    }

    [Fact]
    public void Lock_registry_resolves_an_alias_to_the_registered_capability()
    {
        var registry = new DatabaseMigrationLockProviderRegistry(
            [new FakeMigrationLockProvider(CanonicalId)],
            CreateResolver());

        Assert.True(registry.TryGetProvider(AliasId, out var byAlias));
        Assert.True(registry.TryGetProvider(CanonicalId, out var byCanonicalId));
        Assert.Same(byAlias, byCanonicalId);
    }

    [Fact]
    public void Lock_registry_registered_under_an_alias_is_found_by_the_canonical_id()
    {
        var registry = new DatabaseMigrationLockProviderRegistry(
            [new FakeMigrationLockProvider(AliasId)],
            CreateResolver());

        Assert.True(registry.TryGetProvider(CanonicalId, out var provider));
        Assert.NotNull(provider);
    }

    [Fact]
    public void Lock_registry_rejects_a_canonical_and_alias_duplicate()
    {
        Assert.Throws<ArgumentException>(() => new DatabaseMigrationLockProviderRegistry(
            [new FakeMigrationLockProvider(CanonicalId), new FakeMigrationLockProvider(AliasId)],
            CreateResolver()));
    }

    [Fact]
    public void Lock_registry_fails_closed_when_the_capability_is_not_registered()
    {
        var registry = new DatabaseMigrationLockProviderRegistry([], CreateResolver());

        Assert.False(registry.TryGetProvider(AliasId, out var provider));
        Assert.Null(provider);
    }

    [Fact]
    public async Task Third_party_alias_flows_from_create_through_migration_lock_acquisition()
    {
        using var directory = TemporaryDirectory.Create();
        var serviceId = ServiceId.Parse("signacore");

        // A third-party descriptor declares canonical id "PostgreSQL" with alias "postgres".
        var bootstrapProvider = new FakeBootstrapProvider(
            DatabaseProviderIdResolverTests.Descriptor(CanonicalId, AliasId));
        var providerRegistry = new BootstrapDatabaseProviderRegistry([bootstrapProvider]);
        var resolver = providerRegistry.ProviderIdResolver;

        var store = CreateStore(directory, providerRegistry);
        var manager = new BootstrapConfigurationManager(
            store,
            InstanceId.Parse("Node-A3"),
            new BootstrapDatabaseCandidateValidator(providerRegistry));

        // Operations hand the alias to the resolver-aware store.
        await manager.CreateAsync(
            new BootstrapCreateRequest(
                new BootstrapDatabaseConfiguration(AliasId, "16", "Host=db;Password=p"),
                "master-key"),
            CancellationToken.None);

        Assert.Equal(CanonicalId, ReadPersistedProvider(store));
        Assert.Equal(CanonicalId, bootstrapProvider.LastValidated!.Provider);
        Assert.Equal(CanonicalId, manager.GetStatus().Provider);

        var loaded = store.Load();
        Assert.Equal(CanonicalId, loaded.Database.Provider);

        // Target preparation resolves the same canonical id.
        var preparationRegistry = new DatabaseTargetPreparationProviderRegistry(
            [new FakePreparationProvider(CanonicalId)],
            resolver);
        Assert.True(preparationRegistry.TryGetProvider(loaded.Database.Provider, out var preparationProvider));
        Assert.NotNull(preparationProvider);

        // Migration lock acquisition, the original failure, now succeeds.
        var lockRegistry = new DatabaseMigrationLockProviderRegistry(
            [new FakeMigrationLockProvider(CanonicalId)],
            resolver);
        var orchestrator = new DatabaseMigrationOrchestrator(
            new FakeMigrationExecutor(
                [MigrationObservationState.Empty, MigrationObservationState.CurrentVersionCompatible]),
            lockRegistry);

        var result = await orchestrator.OrchestrateMigrationAsync(
            serviceId,
            loaded.Database,
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.True(result.Succeeded);
    }

    [Fact]
    public async Task Migration_still_fails_closed_when_only_the_bootstrap_provider_is_registered()
    {
        var providerRegistry = new BootstrapDatabaseProviderRegistry(
            [new FakeBootstrapProvider(DatabaseProviderIdResolverTests.Descriptor(CanonicalId, AliasId))]);
        var orchestrator = new DatabaseMigrationOrchestrator(
            new FakeMigrationExecutor(MigrationObservationState.Empty),
            new DatabaseMigrationLockProviderRegistry([], providerRegistry.ProviderIdResolver));

        var result = await orchestrator.OrchestrateMigrationAsync(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(AliasId, "16", "Host=db;Password=p"),
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
    }

    private static BootstrapDatabaseProviderRegistry CreateProviderRegistry() =>
        new([new FakeBootstrapProvider(DatabaseProviderIdResolverTests.Descriptor(CanonicalId, AliasId))]);

    private static DatabaseProviderIdResolver CreateResolver() =>
        CreateProviderRegistry().ProviderIdResolver;

    private static BootstrapFileStore CreateStore(
        TemporaryDirectory directory,
        BootstrapDatabaseProviderRegistry providerRegistry)
    {
        var filePath = Path.Combine(directory.Path, "config", "signacore.bootstrap.json");
        Directory.CreateDirectory(Path.GetDirectoryName(filePath)!);
        return new BootstrapFileStore(ServiceId.Parse("signacore"), providerRegistry, filePath);
    }

    private static BootstrapConfiguration CreateConfiguration(
        string provider,
        string connectionString = "Host=db;Password=p") =>
        new(
            ServiceId.Parse("signacore"),
            new BootstrapDatabaseConfiguration(provider, "15", connectionString),
            "test-master-key");

    private static string? ReadPersistedProvider(BootstrapFileStore store)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(store.FilePath));
        return document.RootElement.GetProperty("Database").GetProperty("Provider").GetString();
    }

    private sealed class FakePreparationProvider : IDatabaseTargetPreparationProvider
    {
        public FakePreparationProvider(string providerId) => ProviderId = providerId;

        public string ProviderId { get; }

        public BootstrapDatabaseTargetKind TargetKind => BootstrapDatabaseTargetKind.ServerDatabase;

        public ValueTask<DatabaseTargetObservation> ObserveAsync(
            BootstrapDatabaseConfiguration target,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DatabaseTargetObservation.TargetMissing());

        public ValueTask<DatabaseTargetPreparationResult> PrepareAsync(
            DatabaseTargetPreparationRequest request,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult(DatabaseTargetPreparationResult.Success(DatabaseTargetPreparationOutcome.Created));
    }
}
