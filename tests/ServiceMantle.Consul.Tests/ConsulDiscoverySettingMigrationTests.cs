using System.Text.Json;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Consul.Tests;

/// <summary>
/// In-memory conversion example for the retired <c>consul.*</c> setting keys. This listing is the
/// tested source of truth for the copies in README.md and docs/contracts/consul-registration-lifecycle.md.
/// It maps rows key by key, resolves keys with the store's normalization, type-checks the eight
/// migration keys against the catalog, and re-protects the credential envelope; database reads and
/// the final commit stay with the consumer.
/// </summary>
internal static class ConsulDiscoverySettingMigration
{
    internal const string LegacyCredentialKey = "consul.token";

    internal static readonly IReadOnlyDictionary<string, string> LegacyKeyMap =
        new Dictionary<string, string>
        {
            ["consul.enabled"] = ConsulSettingDefinitions.Enabled,
            ["consul.endpoint"] = ConsulSettingDefinitions.Endpoint,
            ["consul.token"] = ConsulSettingDefinitions.Token,
            ["consul.service-name"] = ConsulSettingDefinitions.ServiceName,
            ["consul.address"] = ConsulSettingDefinitions.Address,
            ["consul.port"] = ConsulSettingDefinitions.Port,
            ["consul.health-path"] = ConsulSettingDefinitions.HealthPath,
            ["consul.health-scheme"] = ConsulSettingDefinitions.HealthScheme
        };

    internal static bool TryConvert(
        ServiceId serviceId,
        string rootKey,
        ServiceSettingDefinitionRegistry registry,
        long targetVersion,
        IReadOnlyList<PersistedServiceSettingValue> persistedRows,
        out IReadOnlyList<PersistedServiceSettingValue>? migratedRows,
        out string? errorCode,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(persistedRows);
        if (targetVersion < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(targetVersion));
        }

        migratedRows = null;
        errorCode = null;

        // The rows must come from one complete store version read inside the consumer's own unit of
        // work. The consumer commits the full mapping once under one consistent new version.
        long? version = null;
        var migrated = new List<PersistedServiceSettingValue>(persistedRows.Count);
        var targetKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        // An empty row set is a valid default-disabled store, so cancellation is observed even when
        // no row is ever visited.
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var row in persistedRows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (row.Version < 0 || (version is not null && row.Version != version))
            {
                errorCode = "migration.mixed_version";
                return false;
            }

            version = row.Version;

            // The store and the loader resolve keys by trimming and lowercasing them; retired-key
            // variants must map, re-protect, and collide with their neutral target key the same way.
            var normalizedKey = NormalizeKey(row.Key);
            var targetKey = LegacyKeyMap.TryGetValue(normalizedKey, out var mapped) ? mapped : row.Key;
            if (!targetKeys.Add(NormalizeKey(targetKey)))
            {
                errorCode = "migration.target_key_conflict";
                return false;
            }

            if (registry.TryGetDefinition(targetKey, out var definition))
            {
                // Mirror the loader's materialization rules: a row whose declared type is undefined
                // or differs from the catalog aborts here instead of reaching the consumer as a
                // committed row the new version cannot load.
                if (!Enum.IsDefined(row.ValueType) || row.ValueType != definition!.ValueType)
                {
                    errorCode = normalizedKey == LegacyCredentialKey
                        ? "migration.credential_type_invalid"
                        : "migration.type_mismatch";
                    return false;
                }
            }

            if (normalizedKey != LegacyCredentialKey)
            {
                migrated.Add(new PersistedServiceSettingValue(targetKey, targetVersion, row.ValueType, row.Value));
                continue;
            }

            if (row.ValueType != ServiceSettingValueType.String)
            {
                errorCode = "migration.credential_type_invalid";
                return false;
            }

            try
            {
                // The envelope is bound to the setting key as its protection purpose, so it must be
                // decrypted under the retired purpose and re-encrypted under the neutral key. The
                // plaintext stays local to this loop; only safe failure codes are ever returned.
                var plaintext = new SensitiveValueProtector(serviceId, LegacyCredentialKey)
                    .Unprotect(row.Value, rootKey, cancellationToken);
                var envelope = new SensitiveValueProtector(serviceId, targetKey)
                    .Protect(plaintext, rootKey, cancellationToken);
                migrated.Add(new PersistedServiceSettingValue(targetKey, targetVersion, row.ValueType, envelope));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (SensitiveValueProtectionException)
            {
                errorCode = "migration.credential_decryption_failed";
                return false;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        migratedRows = migrated.AsReadOnly();
        return true;
    }

    private static string NormalizeKey(string key) => key.Trim().ToLowerInvariant();
}

public sealed class ConsulDiscoverySettingMigrationTests
{
    public static TheoryData<string, ServiceSettingValueType, string?, bool> CatalogRows => new()
    {
        { ConsulSettingDefinitions.Enabled, ServiceSettingValueType.Boolean, "false", false },
        { ConsulSettingDefinitions.Endpoint, ServiceSettingValueType.String, null, false },
        { ConsulSettingDefinitions.Token, ServiceSettingValueType.String, null, true },
        { ConsulSettingDefinitions.ServiceName, ServiceSettingValueType.String, null, false },
        { ConsulSettingDefinitions.Address, ServiceSettingValueType.String, null, false },
        { ConsulSettingDefinitions.Port, ServiceSettingValueType.Number, null, false },
        { ConsulSettingDefinitions.HealthPath, ServiceSettingValueType.String, "/health/ready", false },
        { ConsulSettingDefinitions.HealthScheme, ServiceSettingValueType.String, "http", false }
    };

    [Theory]
    [MemberData(nameof(CatalogRows))]
    public void Catalog_registers_neutral_keys_with_unchanged_metadata(
        string key, ServiceSettingValueType type, string? defaultValue, bool isSensitive)
    {
        using var fixture = new ConsulFixture();
        Assert.True(fixture.Registry.TryGetDefinition(key, out var definition));
        Assert.Equal(type, definition!.ValueType);
        Assert.Equal(defaultValue, definition.DefaultValue);
        Assert.False(definition.IsRequired);
        Assert.Equal(isSensitive, definition.IsSensitive);
        Assert.True(definition.RequiresRestart);
    }

    [Fact]
    public void Catalog_registers_no_legacy_key_aliases()
    {
        using var fixture = new ConsulFixture();
        Assert.Equal(ConsulDiscoverySettingMigration.LegacyKeyMap.Count, fixture.Registry.Definitions.Count);
        foreach (var legacyKey in ConsulDiscoverySettingMigration.LegacyKeyMap.Keys)
        {
            Assert.False(fixture.Registry.TryGetDefinition(legacyKey, out _));
        }
    }

    private static IReadOnlyList<PersistedServiceSettingValue> NeutralRows(
        long version, string token = ConsulFixture.Secret) =>
    [
        new(ConsulSettingDefinitions.Enabled, version, ServiceSettingValueType.Boolean, "true"),
        new(ConsulSettingDefinitions.Endpoint, version, ServiceSettingValueType.String, "https://agent.example:8501"),
        new(ConsulSettingDefinitions.Token, version, ServiceSettingValueType.String,
            new SensitiveValueProtector(ConsulFixture.Service, ConsulSettingDefinitions.Token)
                .Protect(token, ConsulFixture.RootKey)),
        new(ConsulSettingDefinitions.ServiceName, version, ServiceSettingValueType.String, "orders-api"),
        new(ConsulSettingDefinitions.Address, version, ServiceSettingValueType.String, "orders.example"),
        new(ConsulSettingDefinitions.Port, version, ServiceSettingValueType.Number, "8080")
    ];

    private static IReadOnlyList<PersistedServiceSettingValue> LegacyRows(
        long version, string token = ConsulFixture.Secret) =>
    [
        new("consul.enabled", version, ServiceSettingValueType.Boolean, "true"),
        new("consul.endpoint", version, ServiceSettingValueType.String, "https://agent.example:8501"),
        new("consul.token", version, ServiceSettingValueType.String,
            new SensitiveValueProtector(ConsulFixture.Service, ConsulDiscoverySettingMigration.LegacyCredentialKey)
                .Protect(token, ConsulFixture.RootKey)),
        new("consul.service-name", version, ServiceSettingValueType.String, "orders-api"),
        new("consul.address", version, ServiceSettingValueType.String, "orders.example"),
        new("consul.port", version, ServiceSettingValueType.Number, "8080"),
        new("consul.health-path", version, ServiceSettingValueType.String, "/health/ready"),
        new("consul.health-scheme", version, ServiceSettingValueType.String, "http")
    ];

    private static IReadOnlyList<PersistedServiceSettingValue> VariantLegacyRows(
        long version, string token = ConsulFixture.Secret) =>
    [
        new(" CONSUL.ENABLED ", version, ServiceSettingValueType.Boolean, "true"),
        new("CONSUL.Endpoint", version, ServiceSettingValueType.String, "https://agent.example:8501"),
        new("\tconsul.token\n", version, ServiceSettingValueType.String,
            new SensitiveValueProtector(ConsulFixture.Service, ConsulDiscoverySettingMigration.LegacyCredentialKey)
                .Protect(token, ConsulFixture.RootKey)),
        new("consul.SERVICE-NAME", version, ServiceSettingValueType.String, "orders-api"),
        new(" Consul.Address", version, ServiceSettingValueType.String, "orders.example"),
        new("CONSUL.port", version, ServiceSettingValueType.Number, "8080"),
        new("consul.Health-Path", version, ServiceSettingValueType.String, "/health/ready"),
        new(" consul.HEALTH-SCHEME ", version, ServiceSettingValueType.String, "http")
    ];

    [Fact]
    public async Task A_complete_legacy_snapshot_fails_as_unknown_before_first_activation()
    {
        using var fixture = new ConsulFixture();
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 1, LegacyRows(1));
        var result = await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal(WellKnownServiceSettingSnapshotErrorCodes.UnknownKey, error.ErrorCode);
        Assert.Null(error.Key);
        Assert.False(fixture.Accessor.TryGetCurrent(out _));
        Assert.Equal(ConsulConfigurationError.SnapshotUnavailable,
            Assert.Throws<ConsulConfigurationException>(() => fixture.Provider.CreateClient()).Error);
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
    }

    public static TheoryData<string, string> LegacyKeys => new(
        ConsulDiscoverySettingMigration.LegacyKeyMap.Select(pair => (pair.Key, pair.Value)));

    [Theory]
    [MemberData(nameof(LegacyKeys))]
    public async Task Each_legacy_key_fails_persistence_reading_even_when_replacing_its_neutral_counterpart(
        string legacyKey, string neutralKey)
    {
        var rows = NeutralRows(1)
            .Where(row => row.Key != neutralKey)
            .Concat(LegacyRows(1).Where(row => row.Key == legacyKey))
            .ToList();
        using var fixture = new ConsulFixture();
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 1, rows);
        var result = await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownServiceSettingSnapshotErrorCodes.UnknownKey,
            Assert.Single(result.Errors).ErrorCode);
        Assert.False(fixture.Accessor.TryGetCurrent(out _));
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
    }

    [Fact]
    public async Task Mixed_snapshots_with_legacy_keys_never_silently_succeed_as_disabled()
    {
        using var fixture = new ConsulFixture();
        var rows = LegacyRows(1)
            .Where(row => row.Key != "consul.enabled")
            .Concat([new(ConsulSettingDefinitions.Enabled, 1, ServiceSettingValueType.Boolean, "false")]);
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 1, rows);
        var result = await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(WellKnownServiceSettingSnapshotErrorCodes.UnknownKey,
            Assert.Single(result.Errors).ErrorCode);
        Assert.False(fixture.Accessor.TryGetCurrent(out _));
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
    }

    [Fact]
    public async Task A_legacy_refresh_failure_retains_the_active_snapshot()
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled(), 1);
        using var original = fixture.Provider.CreateClient();
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 2, LegacyRows(2));
        var result = await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        Assert.Equal(1, original!.SnapshotVersion);
        using var retained = fixture.Provider.CreateClient();
        Assert.Equal(1, retained!.SnapshotVersion);
        Assert.Equal(2, fixture.ClientFactory.Calls);
    }

    [Fact]
    public async Task Moved_legacy_ciphertext_without_reprotection_fails_authentication()
    {
        var legacyEnvelope = LegacyRows(1).Single(row => row.Key == ConsulDiscoverySettingMigration.LegacyCredentialKey);
        var rows = NeutralRows(1)
            .Where(row => row.Key != ConsulSettingDefinitions.Token)
            .Concat([new(ConsulSettingDefinitions.Token, 1, ServiceSettingValueType.String, legacyEnvelope.Value)]);
        using var fixture = new ConsulFixture();
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 1, rows);
        var result = await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.False(result.Succeeded);
        var error = Assert.Single(result.Errors);
        Assert.Equal(WellKnownServiceSettingSnapshotErrorCodes.SensitiveAuthenticationFailed, error.ErrorCode);
        Assert.Equal(ConsulSettingDefinitions.Token, error.Key);
        Assert.Equal(ConsulConfigurationError.SnapshotUnavailable,
            Assert.Throws<ConsulConfigurationException>(() => fixture.Provider.CreateClient()).Error);
        Assert.Equal(0, fixture.ClientFactory.Resolutions);
    }

    [Fact]
    public async Task Conversion_reprotects_the_credential_and_the_result_activates_end_to_end()
    {
        using var fixture = new ConsulFixture();
        Assert.True(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, LegacyRows(1),
            out var migrated, out var errorCode, TestContext.Current.CancellationToken));
        Assert.Null(errorCode);
        Assert.Equal(ConsulDiscoverySettingMigration.LegacyKeyMap.Count, migrated!.Count);
        Assert.All(migrated, row => Assert.DoesNotContain("consul.", row.Key));
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 2, migrated);
        var result = await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken);
        Assert.True(result.Succeeded, result.ToString());
        using var client = fixture.Provider.CreateClient();
        Assert.Equal(2, client!.SnapshotVersion);
        Assert.Equal(ConsulFixture.Secret, fixture.ClientFactory.Configuration!.GetToken());
        Assert.DoesNotContain(ConsulFixture.Secret, JsonSerializer.Serialize(migrated));
    }

    [Fact]
    public async Task Conversion_maps_retired_key_variants_the_way_the_store_resolves_them()
    {
        using var fixture = new ConsulFixture();
        Assert.True(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, VariantLegacyRows(1),
            out var migrated, out var errorCode, TestContext.Current.CancellationToken));
        Assert.Null(errorCode);
        Assert.Equal(ConsulDiscoverySettingMigration.LegacyKeyMap.Count, migrated!.Count);
        Assert.Equal(
            ConsulDiscoverySettingMigration.LegacyKeyMap.Values.OrderBy(key => key, StringComparer.Ordinal),
            migrated.Select(row => row.Key).OrderBy(key => key, StringComparer.Ordinal));
        Assert.All(migrated, row => Assert.DoesNotContain("consul.", row.Key));
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 2, migrated);
        Assert.True((await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken)).Succeeded);
        using var client = fixture.Provider.CreateClient();
        Assert.Equal(ConsulFixture.Secret, fixture.ClientFactory.Configuration!.GetToken());
    }

    [Fact]
    public void Conversion_fails_when_key_variants_collapse_onto_one_target_key()
    {
        using var fixture = new ConsulFixture();

        // A whitespace variant of the neutral key conflicts with the mapped retired key.
        var whitespaceVariant = new List<PersistedServiceSettingValue>(LegacyRows(1))
        {
            new(" discovery.endpoint ", 1, ServiceSettingValueType.String, "https://agent.example:8501")
        };
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, whitespaceVariant,
            out var migrated, out var errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.target_key_conflict", errorCode);
        Assert.Null(migrated);

        // A case variant of the neutral key conflicts after normalization.
        var caseVariant = new List<PersistedServiceSettingValue>(LegacyRows(1))
        {
            new("DISCOVERY.ENDPOINT", 1, ServiceSettingValueType.String, "https://agent.example:8501")
        };
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, caseVariant,
            out migrated, out errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.target_key_conflict", errorCode);
        Assert.Null(migrated);

        // Two retired credential variants collapse onto one credential row.
        var credentialVariant = new List<PersistedServiceSettingValue>(LegacyRows(1))
        {
            new("CONSUL.TOKEN", 1, ServiceSettingValueType.String, "not-reprotection-relevant")
        };
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, credentialVariant,
            out migrated, out errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.target_key_conflict", errorCode);
        Assert.Null(migrated);

        // Unrelated product keys the store would normalize onto one row also conflict.
        var productVariant = new List<PersistedServiceSettingValue>
        {
            new("product.feature", 1, ServiceSettingValueType.Boolean, "true"),
            new(" PRODUCT.FEATURE ", 1, ServiceSettingValueType.Boolean, "true")
        };
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, productVariant,
            out migrated, out errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.target_key_conflict", errorCode);
        Assert.Null(migrated);
    }

    [Fact]
    public void Conversion_rejects_non_credential_rows_whose_type_is_unknown_or_mismatched()
    {
        using var fixture = new ConsulFixture();

        var mismatchedType = LegacyRows(1).Select(row => row.Key == "consul.enabled"
            ? new PersistedServiceSettingValue(row.Key, row.Version, ServiceSettingValueType.String, row.Value)
            : row).ToList();
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, mismatchedType,
            out var migrated, out var errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.type_mismatch", errorCode);
        Assert.Null(migrated);

        var undefinedType = LegacyRows(1).Select(row => row.Key == "consul.enabled"
            ? new PersistedServiceSettingValue(row.Key, row.Version, (ServiceSettingValueType)999, row.Value)
            : row).ToList();
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, undefinedType,
            out migrated, out errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.type_mismatch", errorCode);
        Assert.Null(migrated);

        // Rows already stored under a neutral key are checked against the same catalog.
        var mistypedNeutralRow = new List<PersistedServiceSettingValue>
        {
            new(ConsulSettingDefinitions.Enabled, 1, ServiceSettingValueType.String, "true"),
            new(ConsulSettingDefinitions.Endpoint, 1, ServiceSettingValueType.String, "https://agent.example:8501")
        };
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, mistypedNeutralRow,
            out migrated, out errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.type_mismatch", errorCode);
        Assert.Null(migrated);
    }

    [Fact]
    public async Task Conversion_without_a_credential_row_maps_the_remaining_keys()
    {
        using var fixture = new ConsulFixture();
        var legacy = LegacyRows(1).Where(row => row.Key != ConsulDiscoverySettingMigration.LegacyCredentialKey).ToList();
        Assert.True(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, legacy,
            out var migrated, out _, TestContext.Current.CancellationToken));
        Assert.Equal(legacy.Count, migrated!.Count);
        Assert.DoesNotContain(migrated, row => row.Key == ConsulSettingDefinitions.Token);
        fixture.SnapshotSource.Read = new(ConsulFixture.Service, 2, migrated);
        Assert.True((await fixture.Loader.RefreshAsync(TestContext.Current.CancellationToken)).Succeeded);
        using var client = fixture.Provider.CreateClient();
        Assert.False(fixture.ClientFactory.Configuration!.HasToken);
    }

    [Fact]
    public void Conversion_keeps_unrelated_product_keys_unchanged()
    {
        using var fixture = new ConsulFixture();
        var legacy = new List<PersistedServiceSettingValue>(LegacyRows(1))
        {
            new("product.feature", 1, ServiceSettingValueType.Boolean, "true"),
            new("product.name", 1, ServiceSettingValueType.String, "orders")
        };
        Assert.True(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, legacy,
            out var migrated, out _, TestContext.Current.CancellationToken));
        Assert.Equal(legacy.Count, migrated!.Count);
        Assert.Equal("true", migrated.Single(row => row.Key == "product.feature").Value);
        Assert.Equal("orders", migrated.Single(row => row.Key == "product.name").Value);
    }

    [Fact]
    public void Conversion_fails_when_a_target_key_already_exists()
    {
        using var fixture = new ConsulFixture();
        var legacy = new List<PersistedServiceSettingValue>(LegacyRows(1))
        {
            new(ConsulSettingDefinitions.Endpoint, 1, ServiceSettingValueType.String, "https://agent.example:8501")
        };
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, legacy,
            out var migrated, out var errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.target_key_conflict", errorCode);
        Assert.Null(migrated);
    }

    [Fact]
    public void Conversion_fails_without_partial_output_when_the_root_key_is_wrong()
    {
        using var fixture = new ConsulFixture();
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, "wrong-root-key", fixture.Registry, 2, LegacyRows(1),
            out var migrated, out var errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.credential_decryption_failed", errorCode);
        Assert.Null(migrated);
    }

    [Fact]
    public void Conversion_fails_without_partial_output_when_the_ciphertext_is_corrupt()
    {
        using var fixture = new ConsulFixture();
        var corrupt = LegacyRows(1).Select(row => row.Key == ConsulDiscoverySettingMigration.LegacyCredentialKey
            ? new PersistedServiceSettingValue(row.Key, row.Version, row.ValueType, "sm:v1:not-base64!!")
            : row).ToList();
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, corrupt,
            out var migrated, out var errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.credential_decryption_failed", errorCode);
        Assert.Null(migrated);
    }

    [Fact]
    public void Conversion_fails_on_mixed_row_versions_or_a_non_string_credential()
    {
        using var fixture = new ConsulFixture();
        var mixed = new List<PersistedServiceSettingValue>(LegacyRows(1))
        {
            new("consul.health-path", 2, ServiceSettingValueType.String, "/health/ready")
        };
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, mixed,
            out var migrated, out var errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.mixed_version", errorCode);
        Assert.Null(migrated);

        var mistyped = LegacyRows(1).Select(row => row.Key == ConsulDiscoverySettingMigration.LegacyCredentialKey
            ? new PersistedServiceSettingValue(row.Key, row.Version, ServiceSettingValueType.Number, row.Value)
            : row).ToList();
        Assert.False(ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, mistyped,
            out migrated, out errorCode, TestContext.Current.CancellationToken));
        Assert.Equal("migration.credential_type_invalid", errorCode);
        Assert.Null(migrated);
    }

    [Fact]
    public void Conversion_propagates_caller_cancellation_without_a_partial_result()
    {
        using var fixture = new ConsulFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        IReadOnlyList<PersistedServiceSettingValue>? migrated = null;
        var outcome = Assert.Throws<OperationCanceledException>(() => ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, LegacyRows(1),
            out migrated, out _, cancelled.Token));
        Assert.Equal(cancelled.Token, outcome.CancellationToken);
        Assert.Null(migrated);
    }

    [Fact]
    public void Conversion_cancels_before_touching_an_empty_row_set()
    {
        using var fixture = new ConsulFixture();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        IReadOnlyList<PersistedServiceSettingValue>? migrated = null;
        var outcome = Assert.Throws<OperationCanceledException>(() => ConsulDiscoverySettingMigration.TryConvert(
            ConsulFixture.Service, ConsulFixture.RootKey, fixture.Registry, 2, [],
            out migrated, out _, cancelled.Token));
        Assert.Equal(cancelled.Token, outcome.CancellationToken);
        Assert.Null(migrated);
    }

    [Fact]
    public async Task Credential_projection_remains_write_only_under_the_neutral_key()
    {
        using var fixture = new ConsulFixture();
        await fixture.ActivateAsync(ConsulFixture.Enabled());
        var query = new ServiceSettingQueryService(fixture.Registry, fixture.Loader);
        var current = await query.GetCurrentAsync(TestContext.Current.CancellationToken);
        Assert.True(current.Succeeded);
        var credential = Assert.Single(current.Values, value => value.Key == ConsulSettingDefinitions.Token);
        Assert.True(credential.IsSensitive);
        Assert.True(credential.HasValue);
        Assert.Null(credential.Value);
        Assert.DoesNotContain(ConsulFixture.Secret, JsonSerializer.Serialize(current));
        Assert.DoesNotContain(ConsulFixture.Secret, JsonSerializer.Serialize(query.GetDefinitions()));
    }
}
