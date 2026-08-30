using System.Data.Common;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.DataProtection.KeyManagement;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using ServiceMantle.Persistence.EntityFrameworkCore;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

public sealed class DataProtectionKeyRepositoryTests
{
    private const string RootKey = "bootstrap-root-key-for-data-protection-tests-4fba32";
    private const string OtherRootKey = "different-bootstrap-root-key-for-tests-392ba1";
    private static readonly ServiceId Service = ServiceId.Parse("orders-api");

    [Fact]
    public void Mapping_uses_service_and_key_identity_without_instance_identity()
    {
        using var context = new KeyDbContext(
            new DbContextOptionsBuilder<KeyDbContext>().UseSqlite("Data Source=:memory:").Options);

        var entity = context.Model.FindEntityType(typeof(DataProtectionKeyEntity));

        Assert.NotNull(entity);
        Assert.Equal("service_data_protection_keys", entity.GetTableName());
        Assert.Equal(
            [nameof(DataProtectionKeyEntity.ServiceId), nameof(DataProtectionKeyEntity.KeyId)],
            entity.FindPrimaryKey()!.Properties.Select(property => property.Name));
        Assert.DoesNotContain(
            entity.GetProperties(),
            property => property.Name.Contains("Instance", StringComparison.OrdinalIgnoreCase));
        Assert.Equal(64, entity.FindProperty(nameof(DataProtectionKeyEntity.KeyId))!.GetMaxLength());
        Assert.Equal("encrypted_xml", entity.FindProperty(nameof(DataProtectionKeyEntity.EncryptedXml))!.GetColumnName());
    }

    [Fact]
    public async Task Store_encrypts_complete_xml_and_load_round_trips_it()
    {
        await using var harness = await Harness.CreateAsync();
        var keyId = Guid.NewGuid();
        const string keyMaterial = "plaintext-master-key-material";
        var element = CreateKey(keyId, keyMaterial);
        var repository = harness.Repository(Service, RootKey);

        repository.StoreElement(element, $"key-{keyId:D}");

        await using var context = harness.Factory().CreateDbContext();
        var row = await context.Set<DataProtectionKeyEntity>().SingleAsync(TestContext.Current.CancellationToken);
        var loaded = Assert.Single(repository.GetAllElements());
        Assert.Equal(Service.Value, row.ServiceId);
        Assert.Equal($"key-{keyId:D}", row.KeyId);
        Assert.StartsWith("sm:v1:", row.EncryptedXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<key", row.EncryptedXml, StringComparison.Ordinal);
        Assert.DoesNotContain(keyMaterial, row.EncryptedXml, StringComparison.Ordinal);
        Assert.True(XNode.DeepEquals(element, loaded));
    }

    [Fact]
    public async Task Wrong_root_key_and_damaged_ciphertext_fail_closed_without_leaking_material()
    {
        await using var harness = await Harness.CreateAsync();
        var keyId = Guid.NewGuid();
        const string keyMaterial = "do-not-leak-key-xml-material";
        harness.Repository(Service, RootKey).StoreElement(
            CreateKey(keyId, keyMaterial),
            $"key-{keyId:D}");

        var wrongRootException = Assert.Throws<DataProtectionKeyRepositoryException>(
            () => harness.Repository(Service, OtherRootKey).GetAllElements());

        string ciphertext;
        await using (var context = harness.Factory().CreateDbContext())
        {
            var row = await context.Set<DataProtectionKeyEntity>()
                .SingleAsync(TestContext.Current.CancellationToken);
            ciphertext = row.EncryptedXml;
            row.EncryptedXml = "sm:v1:not-valid-ciphertext";
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var damagedException = Assert.Throws<DataProtectionKeyRepositoryException>(
            () => harness.Repository(Service, RootKey).GetAllElements());
        foreach (var exception in new[] { wrongRootException, damagedException })
        {
            Assert.Equal(
                WellKnownDataProtectionKeyRepositoryErrorCodes.DecryptionFailed,
                exception.ErrorCode);
            Assert.DoesNotContain(RootKey, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(OtherRootKey, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(ciphertext, exception.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(keyMaterial, exception.ToString(), StringComparison.Ordinal);
        }
    }

    [Fact]
    public async Task Service_key_rings_are_invisible_and_ciphertext_is_bound_to_its_service()
    {
        await using var harness = await Harness.CreateAsync();
        var otherService = ServiceId.Parse("billing-api");
        var keyId = Guid.NewGuid();
        harness.Repository(Service, RootKey).StoreElement(
            CreateKey(keyId, "service-a-key-material"),
            $"key-{keyId:D}");

        Assert.Empty(harness.Repository(otherService, RootKey).GetAllElements());

        await using (var context = harness.Factory().CreateDbContext())
        {
            var source = await context.Set<DataProtectionKeyEntity>()
                .AsNoTracking()
                .SingleAsync(TestContext.Current.CancellationToken);
            context.Add(new DataProtectionKeyEntity
            {
                ServiceId = otherService.Value,
                KeyId = source.KeyId,
                EncryptedXml = source.EncryptedXml,
            });
            await context.SaveChangesAsync(TestContext.Current.CancellationToken);
        }

        var exception = Assert.Throws<DataProtectionKeyRepositoryException>(
            () => harness.Repository(otherService, RootKey).GetAllElements());
        Assert.Equal(
            WellKnownDataProtectionKeyRepositoryErrorCodes.DecryptionFailed,
            exception.ErrorCode);
        Assert.Single(harness.Repository(Service, RootKey).GetAllElements());
    }

    [Fact]
    public async Task Same_key_id_is_independently_owned_by_different_services()
    {
        await using var harness = await Harness.CreateAsync();
        var otherService = ServiceId.Parse("billing-api");
        var keyId = Guid.NewGuid();
        var serviceAElement = CreateKey(keyId, "service-a-key-material");
        var serviceBElement = CreateKey(keyId, "service-b-key-material");

        harness.Repository(Service, RootKey).StoreElement(serviceAElement, $"key-{keyId:D}");
        harness.Repository(otherService, RootKey).StoreElement(serviceBElement, $"key-{keyId:D}");

        Assert.True(XNode.DeepEquals(
            serviceAElement,
            Assert.Single(harness.Repository(Service, RootKey).GetAllElements())));
        Assert.True(XNode.DeepEquals(
            serviceBElement,
            Assert.Single(harness.Repository(otherService, RootKey).GetAllElements())));
        await using var context = harness.Factory().CreateDbContext();
        Assert.Equal(2, await context.Set<DataProtectionKeyEntity>().CountAsync(
            TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Concurrent_duplicate_creation_has_one_winner_and_one_stable_failure()
    {
        await using var harness = await Harness.CreateAsync();
        var keyId = Guid.NewGuid();
        var element = CreateKey(keyId, "concurrent-key-material");
        var repositoryA = harness.Repository(Service, RootKey);
        var repositoryB = harness.Repository(Service, RootKey);

        var attempts = await Task.WhenAll(
            Task.Run(
                () => Record.Exception(() => repositoryA.StoreElement(
                    new XElement(element),
                    $"key-{keyId:D}")),
                TestContext.Current.CancellationToken),
            Task.Run(
                () => Record.Exception(() => repositoryB.StoreElement(
                    new XElement(element),
                    $"key-{keyId:D}")),
                TestContext.Current.CancellationToken));

        Assert.Single(attempts, exception => exception is null);
        var failure = Assert.IsType<DataProtectionKeyRepositoryException>(
            Assert.Single(attempts, exception => exception is not null));
        Assert.Equal(WellKnownDataProtectionKeyRepositoryErrorCodes.DuplicateKey, failure.ErrorCode);
        await using var context = harness.Factory().CreateDbContext();
        Assert.Equal(
            1,
            await context.Set<DataProtectionKeyEntity>()
                .CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Commit_and_storage_failures_roll_back_and_return_safe_classification()
    {
        await using var harness = await Harness.CreateAsync();
        var keyId = Guid.NewGuid();
        const string secret = "provider-and-key-secret";
        var commitRepository = harness.Repository(
            Service,
            RootKey,
            new ThrowingCommitInterceptor(() => new InvalidOperationException(secret)));

        var commitException = Assert.Throws<DataProtectionKeyRepositoryException>(() =>
            commitRepository.StoreElement(CreateKey(keyId, secret), $"key-{keyId:D}"));
        var storageRepository = harness.Repository(
            Service,
            RootKey,
            beforeSave: () => throw new DbUpdateException(secret));
        var storageKeyId = Guid.NewGuid();
        var storageException = Assert.Throws<DataProtectionKeyRepositoryException>(() =>
            storageRepository.StoreElement(
                CreateKey(storageKeyId, secret),
                $"key-{storageKeyId:D}"));

        Assert.Equal(WellKnownDataProtectionKeyRepositoryErrorCodes.StorageError, commitException.ErrorCode);
        Assert.Equal(WellKnownDataProtectionKeyRepositoryErrorCodes.StorageError, storageException.ErrorCode);
        Assert.DoesNotContain(secret, commitException.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(secret, storageException.ToString(), StringComparison.Ordinal);
        await using var context = harness.Factory().CreateDbContext();
        Assert.Equal(0, await context.Set<DataProtectionKeyEntity>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Invalid_elements_and_root_key_failures_are_stable_and_do_not_write()
    {
        await using var harness = await Harness.CreateAsync();
        var repository = harness.Repository(Service, RootKey);

        var invalid = Assert.Throws<DataProtectionKeyRepositoryException>(() =>
            repository.StoreElement(new XElement("key"), "invalid"));
        var unavailableKeyId = Guid.NewGuid();
        var unavailable = Assert.Throws<DataProtectionKeyRepositoryException>(() =>
            harness.Repository(Service, () => throw new InvalidOperationException("root-key-secret"))
                .StoreElement(
                    CreateKey(unavailableKeyId, "xml-secret"),
                    $"key-{unavailableKeyId:D}"));

        Assert.Equal(WellKnownDataProtectionKeyRepositoryErrorCodes.InvalidElement, invalid.ErrorCode);
        Assert.Equal(WellKnownDataProtectionKeyRepositoryErrorCodes.RootKeyUnavailable, unavailable.ErrorCode);
        Assert.DoesNotContain("root-key-secret", unavailable.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("xml-secret", unavailable.ToString(), StringComparison.Ordinal);
        await using var context = harness.Factory().CreateDbContext();
        Assert.Equal(0, await context.Set<DataProtectionKeyEntity>().CountAsync(TestContext.Current.CancellationToken));
    }

    [Fact]
    public async Task Builder_extension_configures_repository_and_drives_real_key_generation_safely()
    {
        await using var harness = await Harness.CreateAsync();
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<KeyDbContext>>(harness.Factory());
        services.AddDataProtection()
            .PersistKeysToServiceMantleEfCore<KeyDbContext>(Service, _ => RootKey);

        using var serviceProvider = services.BuildServiceProvider();
        var options = serviceProvider.GetRequiredService<IOptions<KeyManagementOptions>>().Value;

        var repository = Assert.IsType<EfCoreDataProtectionKeyRepository<KeyDbContext>>(
            options.XmlRepository);
        var protector = serviceProvider.GetRequiredService<IDataProtectionProvider>()
            .CreateProtector("repository-integration-test");
        var protectedPayload = protector.Protect("application-payload");

        Assert.Contains(Service.Value, repository.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain(RootKey, repository.ToString(), StringComparison.Ordinal);
        Assert.Equal("application-payload", protector.Unprotect(protectedPayload));
        await using var context = harness.Factory().CreateDbContext();
        var row = await context.Set<DataProtectionKeyEntity>()
            .SingleAsync(TestContext.Current.CancellationToken);
        Assert.StartsWith("sm:v1:", row.EncryptedXml, StringComparison.Ordinal);
        Assert.DoesNotContain("<key", row.EncryptedXml, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RevokeKey_persists_encrypted_revocation_and_reloads_revoked_state()
    {
        await using var harness = await Harness.CreateAsync();
        using (var serviceProvider = BuildServiceProvider(harness))
        {
            var keyManager = serviceProvider.GetRequiredService<IKeyManager>();
            var now = DateTimeOffset.UtcNow;
            var key = keyManager.CreateNewKey(now, now.AddDays(90));

            keyManager.RevokeKey(key.KeyId, "compromised-key-secret-reason");
        }

        await using (var context = harness.Factory().CreateDbContext())
        {
            var rows = await context.Set<DataProtectionKeyEntity>()
                .AsNoTracking()
                .OrderBy(row => row.KeyId)
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(2, rows.Count);
            Assert.Contains(rows, row => row.KeyId.StartsWith("key-", StringComparison.Ordinal));
            Assert.Contains(rows, row => row.KeyId.StartsWith("revocation-", StringComparison.Ordinal));
            Assert.All(rows, row =>
            {
                Assert.StartsWith("sm:v1:", row.EncryptedXml, StringComparison.Ordinal);
                Assert.DoesNotContain("<key", row.EncryptedXml, StringComparison.Ordinal);
                Assert.DoesNotContain("<revocation", row.EncryptedXml, StringComparison.Ordinal);
                Assert.DoesNotContain("compromised-key-secret-reason", row.EncryptedXml, StringComparison.Ordinal);
            });
        }

        using var reloadedProvider = BuildServiceProvider(harness);
        var reloadedKey = Assert.Single(
            reloadedProvider.GetRequiredService<IKeyManager>().GetAllKeys());
        Assert.True(reloadedKey.IsRevoked);
    }

    [Fact]
    public async Task RevokeAllKeys_persists_encrypted_revocation_and_reloads_all_keys_as_revoked()
    {
        await using var harness = await Harness.CreateAsync();
        using (var serviceProvider = BuildServiceProvider(harness))
        {
            var keyManager = serviceProvider.GetRequiredService<IKeyManager>();
            var now = DateTimeOffset.UtcNow;
            keyManager.CreateNewKey(now, now.AddDays(90));
            keyManager.CreateNewKey(now.AddSeconds(1), now.AddDays(91));

            keyManager.RevokeAllKeys(now.AddMinutes(1), "mass-revocation-secret-reason");
        }

        await using (var context = harness.Factory().CreateDbContext())
        {
            var rows = await context.Set<DataProtectionKeyEntity>()
                .AsNoTracking()
                .ToListAsync(TestContext.Current.CancellationToken);
            Assert.Equal(3, rows.Count);
            Assert.Equal(2, rows.Count(row => row.KeyId.StartsWith("key-", StringComparison.Ordinal)));
            Assert.Single(rows, row => row.KeyId.StartsWith("revocation-", StringComparison.Ordinal));
            Assert.All(rows, row =>
            {
                Assert.DoesNotContain("<revocation", row.EncryptedXml, StringComparison.Ordinal);
                Assert.DoesNotContain("mass-revocation-secret-reason", row.EncryptedXml, StringComparison.Ordinal);
            });
        }

        using var reloadedProvider = BuildServiceProvider(harness);
        var reloadedKeys = reloadedProvider.GetRequiredService<IKeyManager>().GetAllKeys();
        Assert.Equal(2, reloadedKeys.Count);
        Assert.All(reloadedKeys, key => Assert.True(key.IsRevoked));
    }

    private static ServiceProvider BuildServiceProvider(Harness harness)
    {
        var services = new ServiceCollection();
        services.AddSingleton<IDbContextFactory<KeyDbContext>>(harness.Factory());
        services.AddDataProtection()
            .PersistKeysToServiceMantleEfCore<KeyDbContext>(Service, _ => RootKey);
        return services.BuildServiceProvider();
    }

    private static XElement CreateKey(Guid keyId, string keyMaterial) =>
        new(
            "key",
            new XAttribute("id", keyId.ToString("D")),
            new XAttribute("version", "1"),
            new XElement("descriptor", new XElement("masterKey", keyMaterial)));

    private sealed class Harness(
        SqliteConnection keeper,
        DbContextOptions<KeyDbContext> options) : IAsyncDisposable
    {
        internal static async Task<Harness> CreateAsync()
        {
            var connectionString = $"Data Source=servicemantle-keys-{Guid.NewGuid():N};Mode=Memory;Cache=Shared;Default Timeout=10";
            var keeper = new SqliteConnection(connectionString);
            await keeper.OpenAsync(TestContext.Current.CancellationToken);
            var options = new DbContextOptionsBuilder<KeyDbContext>()
                .UseSqlite(connectionString)
                .Options;
            await using var context = new KeyDbContext(options);
            await context.Database.EnsureCreatedAsync(TestContext.Current.CancellationToken);
            return new Harness(keeper, options);
        }

        internal KeyDbContextFactory Factory(
            IInterceptor? interceptor = null,
            Action? beforeSave = null)
        {
            if (interceptor is null)
            {
                return new KeyDbContextFactory(options, beforeSave);
            }

            var interceptedOptions = new DbContextOptionsBuilder<KeyDbContext>(options)
                .AddInterceptors(interceptor)
                .Options;
            return new KeyDbContextFactory(interceptedOptions, beforeSave);
        }

        internal EfCoreDataProtectionKeyRepository<KeyDbContext> Repository(
            ServiceId serviceId,
            string rootKey,
            IInterceptor? interceptor = null,
            Action? beforeSave = null) =>
            Repository(serviceId, () => rootKey, interceptor, beforeSave);

        internal EfCoreDataProtectionKeyRepository<KeyDbContext> Repository(
            ServiceId serviceId,
            Func<string> rootKeyResolver,
            IInterceptor? interceptor = null,
            Action? beforeSave = null) =>
            new(Factory(interceptor, beforeSave), serviceId, rootKeyResolver);

        public async ValueTask DisposeAsync() => await keeper.DisposeAsync();
    }

    private sealed class KeyDbContextFactory(
        DbContextOptions<KeyDbContext> options,
        Action? beforeSave = null) : IDbContextFactory<KeyDbContext>
    {
        public KeyDbContext CreateDbContext() => new(options, beforeSave);
    }

    private sealed class KeyDbContext(
        DbContextOptions<KeyDbContext> options,
        Action? beforeSave = null) : DbContext(options)
    {
        public override int SaveChanges()
        {
            beforeSave?.Invoke();
            return base.SaveChanges();
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder) =>
            modelBuilder.AddServiceMantleDataProtectionKeys();
    }

    private sealed class ThrowingCommitInterceptor(Func<Exception> failure) : DbTransactionInterceptor
    {
        public override InterceptionResult TransactionCommitting(
            DbTransaction transaction,
            TransactionEventData eventData,
            InterceptionResult result) => throw failure();
    }
}
