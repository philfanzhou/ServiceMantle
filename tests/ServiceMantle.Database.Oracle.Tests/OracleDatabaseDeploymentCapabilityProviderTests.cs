using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;
using Xunit;

namespace ServiceMantle.Database.Oracle.Tests;

public sealed class OracleDatabaseDeploymentCapabilityProviderTests
{
    private static BootstrapDatabaseConfiguration Target() => new(WellKnownDatabaseProviderIds.Oracle, "23", "User ID=schema-secret;Password=password-secret;Data Source=unreachable-secret");
    [Fact]
    public async Task Registration_is_explicit_idempotent_and_identity_deterministically_unavailable()
    {
        var services = new ServiceCollection();
        Assert.Empty(services);
        services.AddServiceMantleOracleDeploymentCapability().AddServiceMantleOracleDeploymentCapability();
        Assert.Single(services);
        using var container = services.BuildServiceProvider();
        var provider = Assert.Single(container.GetServices<IDatabaseDeploymentCapabilityProvider>());
        Assert.Equal(WellKnownDatabaseProviderIds.Oracle, provider.Capability.ProviderId);
        Assert.Equal(DatabaseDeploymentSupport.SingleAndMultiInstance, provider.Capability.Support);
        Assert.Empty(await provider.GetCanonicalTargetIdentityAsync(Target(), TestContext.Current.CancellationToken));
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(provider));
        Assert.DoesNotContain("secret", provider.ToString()!);
        Assert.Throws<ArgumentNullException>(() => ServiceMantleOracleDeploymentCapabilityServiceCollectionExtensions.AddServiceMantleOracleDeploymentCapability(null!));
        await Assert.ThrowsAsync<ArgumentNullException>(() => provider.GetCanonicalTargetIdentityAsync(null!, default).AsTask());
    }
    [Fact]
    public async Task Caller_cancellation_preserves_the_original_token_without_secret_inner_diagnostics()
    {
        using var source = new CancellationTokenSource(); source.Cancel();
        var exception = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new OracleDatabaseDeploymentCapabilityProvider().GetCanonicalTargetIdentityAsync(Target(), source.Token).AsTask());
        Assert.Equal(source.Token, exception.CancellationToken); Assert.Null(exception.InnerException);
        foreach (var value in new[] { "schema-secret", "password-secret", "unreachable-secret" })
            Assert.DoesNotContain(value, exception.ToString());
    }
    [Theory]
    [InlineData(DatabaseDeploymentMode.SingleInstance, false, false)]
    [InlineData(DatabaseDeploymentMode.MultiInstance, false, false)]
    [InlineData(DatabaseDeploymentMode.MultiInstance, true, true)]
    public async Task Core_rejects_single_instance_or_missing_lock_and_multi_instance_never_calls_identity(DatabaseDeploymentMode mode, bool registerLock, bool succeeds)
    {
        var provider = new CountingProvider(); var executor = new Executor(); var locking = new LockProvider();
        var orchestrator = new DatabaseMigrationOrchestrator(executor, new(registerLock ? [locking] : [], DatabaseProviderIdResolver.Empty), new([provider], DatabaseProviderIdResolver.Empty));
        var result = await orchestrator.OrchestrateMigrationAsync(ServiceId.Parse("oracle-deployment"), Target(), mode, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
        Assert.Equal(succeeds, result.Succeeded);
        Assert.Equal(succeeds ? null : WellKnownMigrationErrorCodes.LockNotSupported, result.ErrorCode);
        Assert.Equal(mode == DatabaseDeploymentMode.SingleInstance ? 1 : 0, provider.Calls);
        Assert.Equal(succeeds ? 1 : 0, executor.Calls);
        Assert.Equal(succeeds ? 1 : 0, locking.Calls);
        Assert.Equal(succeeds, locking.Released);
        Assert.DoesNotContain("secret", JsonSerializer.Serialize(result));
        var undeclared = new DatabaseMigrationOrchestrator(executor, new([], DatabaseProviderIdResolver.Empty), new([], DatabaseProviderIdResolver.Empty));
        Assert.Equal(WellKnownMigrationErrorCodes.LockNotSupported, (await undeclared.OrchestrateMigrationAsync(ServiceId.Parse("oracle-deployment"), Target(), mode, TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken)).ErrorCode);
    }
    private sealed class CountingProvider : IDatabaseDeploymentCapabilityProvider
    {
        private readonly OracleDatabaseDeploymentCapabilityProvider inner = new();
        public int Calls { get; private set; }
        public DatabaseDeploymentCapability Capability => inner.Capability;
        public ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target, CancellationToken token)
        { Calls++; return inner.GetCanonicalTargetIdentityAsync(target, token); }
    }
    private sealed class Executor : IDatabaseMigrationExecutor
    {
        public int Calls { get; private set; }
        public ValueTask<MigrationObservationState> InspectAsync(CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult(MigrationObservationState.CurrentVersionCompatible); }
        public ValueTask ExecuteAsync(CancellationToken cancellationToken = default) => throw new InvalidOperationException();
    }
    private sealed class LockProvider : IDatabaseMigrationLockProvider
    {
        public string ProviderId => WellKnownDatabaseProviderIds.Oracle;
        public int Calls { get; private set; }
        public bool Released { get; private set; }
        public ValueTask<IDatabaseMigrationLock> AcquireAsync(ServiceId serviceId, BootstrapDatabaseConfiguration bootstrap, TimeSpan acquireTimeout, CancellationToken cancellationToken = default)
        { Calls++; return ValueTask.FromResult<IDatabaseMigrationLock>(new Lease(this)); }
        private sealed class Lease(LockProvider owner) : IDatabaseMigrationLock
        {
            public string ProviderId => owner.ProviderId;
            public CancellationToken LeaseLost => CancellationToken.None;
            public ValueTask DisposeAsync() { owner.Released = true; return ValueTask.CompletedTask; }
        }
    }
}
