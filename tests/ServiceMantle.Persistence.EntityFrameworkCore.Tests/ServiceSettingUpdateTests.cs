using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using ServiceMantle.Audit;
using ServiceMantle.Configuration;
using Xunit;

namespace ServiceMantle.Persistence.EntityFrameworkCore.Tests;

public sealed class ServiceSettingUpdateTests
{
    internal static readonly ServiceId Service = ServiceId.Parse("reference-service");
    private const string Secret = "password-only-in-encrypted-storage";
    private static CancellationToken Token => TestContext.Current.CancellationToken;

    [Fact]
    public async Task Batch_encrypts_normalizes_and_audits_but_caller_controls_commit_and_rollback()
    {
        await using var harness = await Harness.CreateAsync();
        await using (var context = harness.Context())
        await using (var transaction = await context.Database.BeginTransactionAsync(Token))
        {
            var result = await Updater(context).UpdateAsync(Command(0,
                ("Secret", Secret), ("count", "02.00"), ("enabled", "TRUE")), Token);
            Assert.True(result.Succeeded);
            Assert.Equal(1, result.Version);
            Assert.Same(transaction, context.Database.CurrentTransaction);
            Assert.Empty(context.ChangeTracker.Entries());
            var setting = await context.Set<ServiceSettingEntity>().AsNoTracking().SingleAsync(Token);
            var values = JsonSerializer.Deserialize<Dictionary<string, string>>(setting.ValuesJson)!;
            Assert.Equal("2", values["count"]);
            Assert.Equal("true", values["enabled"]);
            Assert.StartsWith("sm:v1:", values["secret"], StringComparison.Ordinal);
            Assert.Equal(Secret, new SensitiveValueProtector(Service, "secret").Unprotect(values["secret"], Root.Key, Token));
            Assert.True(setting.RestartRequired);
            var audits = await context.Set<ManagementAuditLogEntity>().AsNoTracking().ToListAsync(Token);
            Assert.Equal(3, audits.Count);
            Assert.All(audits, audit =>
            {
                Assert.Equal("operator-1", audit.OperatorId);
                Assert.Null(audit.OperatorDisplayName);
                Assert.Equal(ManagementAuditOutcome.Success, audit.Outcome);
                Assert.Null(audit.SecurityDescription);
                Assert.Single(JsonSerializer.Deserialize<Dictionary<string, string>>(audit.MetadataJson!)!);
                Assert.DoesNotContain(Secret, audit.MetadataJson!, StringComparison.Ordinal);
            });
            await using var observer = harness.Context();
            Assert.Empty(await observer.Set<ServiceSettingEntity>().ToListAsync(Token));
            await transaction.CommitAsync(Token);
        }
        await using (var context = harness.Context())
        await using (var transaction = await context.Database.BeginTransactionAsync(Token))
        {
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            var result = await Updater(context).UpdateAsync(Command(1, ("secret", null), ("count", null)), Token);
            Assert.True(result.Succeeded);
            var setting = await context.Set<ServiceSettingEntity>().AsNoTracking().SingleAsync(Token);
            Assert.Equal(2, setting.Version);
            Assert.DoesNotContain("secret", setting.ValuesJson, StringComparison.Ordinal);
            Assert.DoesNotContain("count", setting.ValuesJson, StringComparison.Ordinal);
            await transaction.RollbackAsync(Token);
        }
        await using var final = harness.Context();
        Assert.Equal(1, (await final.Set<ServiceSettingEntity>().SingleAsync(Token)).Version);
        Assert.Equal(3, await final.Set<ManagementAuditLogEntity>().CountAsync(Token));
    }

    [Theory]
    [InlineData("count", "bad-sensitive-number", "setting.invalid_number")]
    [InlineData("enabled", "bad-sensitive-boolean", "setting.invalid_boolean")]
    [InlineData("payload", "{secret-invalid-json", "setting.invalid_json")]
    [InlineData("unknown-secret-key", "secret", "setting.unknown")]
    [InlineData("count", "-1", "setting.count_positive")]
    public async Task Invalid_complete_candidate_has_safe_errors_and_no_writes(string key, string? value, string code)
    {
        await using var harness = await Harness.CreateAsync();
        await using var context = harness.Context();
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        var result = await Updater(context).UpdateAsync(Command(0, (key, value)), Token);
        Assert.Equal(ServiceSettingUpdateStatus.ValidationFailed, result.Status);
        Assert.Contains(result.Errors, error => error.ErrorCode == code);
        if (code == "setting.unknown") Assert.Null(Assert.Single(result.Errors).Key);
        Assert.DoesNotContain(value!, result.ToString(), StringComparison.Ordinal);
        Assert.Empty(await context.Set<ServiceSettingEntity>().ToListAsync(Token));
        Assert.Empty(await context.Set<ManagementAuditLogEntity>().ToListAsync(Token));
    }

    [Fact]
    public async Task Duplicate_normalized_keys_are_rejected_before_storage_and_command_is_immutable()
    {
        var input = new Dictionary<string, string?> { ["Count"] = "1", ["count"] = "2" };
        var command = new ServiceSettingUpdateCommand(0, input, ManagementAuditOperator.System());
        input.Clear();
        var fake = new FakeTransaction();
        var result = await new ServiceSettingUpdateService(Service, Registry(), fake).UpdateAsync(command, Token);
        Assert.Equal(ServiceSettingUpdateStatus.ValidationFailed, result.Status);
        Assert.Equal("setting.duplicate", Assert.Single(result.Errors).ErrorCode);
        Assert.Equal(0, fake.LoadCount);
        Assert.Equal(2, command.Changes.Count);
        Assert.Throws<ArgumentException>(() => Command(0));
        Assert.Throws<ArgumentException>(() => new ServiceSettingUpdateCommand(0,
            Enumerable.Range(0, 33).ToDictionary(i => "key" + i, _ => (string?)"value"), ManagementAuditOperator.System()));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_transaction_or_pending_business_changes_are_rejected(bool pending)
    {
        await using var harness = await Harness.CreateAsync();
        await using var context = harness.Context();
        await using var transaction = pending ? await context.Database.BeginTransactionAsync(Token) : null;
        if (pending)
        {
            context.Add(new BusinessEntity { Id = 1, Value = "original" });
            await context.SaveChangesAsync(Token);
            context.ChangeTracker.AutoDetectChangesEnabled = false;
            context.Set<BusinessEntity>().Local.Single().Value = "pending";
        }
        var result = await Updater(context).UpdateAsync(Command(0, ("count", "2")), Token);
        Assert.Equal(pending ? ServiceSettingUpdateStatus.ContextNotClean : ServiceSettingUpdateStatus.TransactionRequired, result.Status);
        Assert.Empty(await context.Set<ServiceSettingEntity>().ToListAsync(Token));
        if (pending)
        {
            Assert.Equal(EntityState.Modified, context.Entry(context.Set<BusinessEntity>().Local.Single()).State);
            Assert.Equal("original", (await context.Set<BusinessEntity>().AsNoTracking().SingleAsync(Token)).Value);
            Assert.False(context.ChangeTracker.AutoDetectChangesEnabled);
        }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Failure_or_cancellation_after_SQL_rolls_back_settings_version_and_audits(bool cancel, bool seed)
    {
        await using var harness = await Harness.CreateAsync();
        var baseline = seed ? 1 : 0;
        if (seed)
        {
            await using var original = harness.Context();
            await using var seedTransaction = await original.Database.BeginTransactionAsync(Token);
            Assert.True((await Updater(original).UpdateAsync(Command(0, ("count", "1")), Token)).Succeeded);
            await seedTransaction.CommitAsync(Token);
        }
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        await using var context = harness.Context(new FailAfterSave(cancel ? cts : null));
        await using var transaction = await context.Database.BeginTransactionAsync(Token);
        if (cancel)
        {
            var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
                Updater(context).UpdateAsync(Command(baseline, ("secret", Secret), ("count", "2")), cts.Token).AsTask());
            Assert.Equal(cts.Token, exception.CancellationToken);
            Assert.Null(exception.InnerException);
        }
        else
        {
            var result = await Updater(context).UpdateAsync(Command(baseline, ("secret", Secret)), Token);
            Assert.Equal(ServiceSettingUpdateStatus.StorageFailed, result.Status);
            Assert.DoesNotContain(Secret, result.ToString(), StringComparison.Ordinal);
        }
        Assert.Empty(context.ChangeTracker.Entries());
        Assert.Equal(baseline, await context.Set<ServiceSettingEntity>().CountAsync(Token));
        if (seed) Assert.Equal(1, (await context.Set<ServiceSettingEntity>().AsNoTracking().SingleAsync(Token)).Version);
        Assert.Equal(baseline, await context.Set<ManagementAuditLogEntity>().CountAsync(Token));
        await transaction.CommitAsync(Token);
        await using var observer = harness.Context();
        Assert.Equal(baseline, await observer.Set<ServiceSettingEntity>().CountAsync(Token));
        Assert.Equal(baseline, await observer.Set<ManagementAuditLogEntity>().CountAsync(Token));
    }

    [Fact]
    public async Task Stale_version_and_exhausted_version_never_apply()
    {
        var fake = new FakeTransaction { Version = 1 };
        var service = new ServiceSettingUpdateService(Service, Registry(), fake);
        Assert.Equal(ServiceSettingUpdateStatus.VersionConflict,
            (await service.UpdateAsync(Command(0, ("count", "2")), Token)).Status);
        fake.Version = long.MaxValue;
        Assert.Equal(ServiceSettingUpdateStatus.VersionExhausted,
            (await service.UpdateAsync(Command(long.MaxValue, ("count", "2")), Token)).Status);
        Assert.Equal(0, fake.ApplyCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_key_or_corrupt_existing_ciphertext_never_applies(bool corrupt)
    {
        var fake = new FakeTransaction();
        if (corrupt)
        {
            fake.Version = 1;
            fake.Values["secret"] = "plaintext-must-not-be-accepted";
        }
        var service = new ServiceSettingUpdateService(Service, Registry(), fake, corrupt ? new Root() : null);
        Assert.Equal(ServiceSettingUpdateStatus.ProtectionFailed,
            (await service.UpdateAsync(Command(fake.Version, ("secret", Secret)), Token)).Status);
        Assert.Equal(0, fake.ApplyCount);
    }

    [Fact]
    public async Task Cancellation_while_getting_root_is_propagated_without_retaining_source_exception()
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Token);
        var fake = new FakeTransaction();
        var service = new ServiceSettingUpdateService(Service, Registry(), fake, new CancellingRoot(cts));
        var exception = await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.UpdateAsync(Command(0, ("secret", Secret)), cts.Token).AsTask());
        Assert.Equal(cts.Token, exception.CancellationToken);
        Assert.Null(exception.InnerException);
        Assert.DoesNotContain(Secret, exception.ToString(), StringComparison.Ordinal);
        Assert.Equal(0, fake.ApplyCount);
    }

    internal static ServiceSettingUpdateService Updater(UpdateContext context) =>
        new(Service, Registry(), new EfCoreServiceSettingUpdateTransaction<UpdateContext>(context), new Root());
    internal static ServiceSettingUpdateCommand Command(long version, params (string Key, string? Value)[] changes) =>
        new(version, changes.ToDictionary(item => item.Key, item => item.Value),
            ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.InteractiveAdmin, "operator-1", Secret));
    private static ServiceSettingDefinitionRegistry Registry() => new([new Definitions()], [new PositiveCount()]);
    private sealed class Definitions : IServiceSettingDefinitionProvider
    {
        public IEnumerable<ServiceSettingDefinition> GetDefinitions() =>
        [
            new("secret", ServiceSettingValueType.String, isSensitive: true, requiresRestart: true),
            new("count", ServiceSettingValueType.Number, isRequired: true, defaultValue: "1"),
            new("enabled", ServiceSettingValueType.Boolean, defaultValue: "false"),
            new("payload", ServiceSettingValueType.Json)
        ];
    }
    private sealed class PositiveCount : IServiceSettingCompositeValidator
    {
        public IEnumerable<ServiceSettingValidationError> Validate(ServiceSettingValidationContext context) =>
            context.Values["count"].GetNumber() > 0 ? [] : [new("count", "setting.count_positive")];
    }
    private sealed class Root : IServiceSettingRootKeySource
    {
        internal const string Key = "external-bootstrap-root-key-for-test-only";
        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken) => ValueTask.FromResult(Key);
    }
    private sealed class CancellingRoot(CancellationTokenSource cts) : IServiceSettingRootKeySource
    {
        public ValueTask<string> GetRootKeyAsync(CancellationToken cancellationToken)
        {
            cts.Cancel();
            throw new InvalidOperationException(Secret);
        }
    }
    private sealed class FakeTransaction : IServiceSettingUpdateTransaction
    {
        public long Version { get; set; }
        public Dictionary<string, string> Values { get; } = [];
        public int LoadCount { get; private set; }
        public int ApplyCount { get; private set; }
        public ValueTask<ServiceSettingStoreSnapshot> LoadAsync(ServiceId serviceId, CancellationToken cancellationToken)
        {
            LoadCount++;
            return ValueTask.FromResult(new ServiceSettingStoreSnapshot(serviceId, Version, Values,
                Version == 0 ? null : DateTimeOffset.UtcNow, Version == 0 ? null : "operator", false));
        }
        public ValueTask<ServiceSettingUpdateResult> ApplyAsync(ServiceId serviceId, ServiceSettingStoreUpdate update,
            IReadOnlyList<ManagementAuditEvent> audits, CancellationToken cancellationToken)
        {
            ApplyCount++;
            return ValueTask.FromResult(ServiceSettingUpdateResult.Applied(Version + 1));
        }
    }
    private sealed class FailAfterSave(CancellationTokenSource? cts) : SaveChangesInterceptor
    {
        public override ValueTask<int> SavedChangesAsync(SaveChangesCompletedEventData eventData, int result,
            CancellationToken cancellationToken = default)
        {
            cts?.Cancel();
            throw new InvalidOperationException(Secret);
        }
    }
    internal sealed class UpdateContext(DbContextOptions<UpdateContext> options) : DbContext(options)
    {
        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            modelBuilder.AddServiceMantleSettings();
            modelBuilder.AddServiceMantleManagementAudit(Database.IsSqlServer()
                ? ManagementAuditDatabaseDialect.SqlServer : ManagementAuditDatabaseDialect.Sqlite);
            modelBuilder.Entity<BusinessEntity>().HasKey(item => item.Id);
        }
    }
    internal sealed class BusinessEntity
    {
        public int Id { get; set; }
        public string Value { get; set; } = "";
    }
    private sealed class Harness : IAsyncDisposable
    {
        private readonly string path = Path.Combine(Path.GetTempPath(), $"sm-batch-{Guid.NewGuid():N}.db");
        public UpdateContext Context(params IInterceptor[] interceptors) => new(
            new DbContextOptionsBuilder<UpdateContext>().UseSqlite($"Data Source={path};Pooling=False")
                .AddInterceptors(interceptors).Options);
        public static async Task<Harness> CreateAsync()
        {
            var harness = new Harness();
            await using var context = harness.Context();
            await context.Database.EnsureCreatedAsync(Token);
            return harness;
        }
        public ValueTask DisposeAsync()
        {
            File.Delete(path);
            return ValueTask.CompletedTask;
        }
    }
}
