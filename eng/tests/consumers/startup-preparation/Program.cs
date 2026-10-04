using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Bootstrap;
using ServiceMantle.Migration;

var gate = new StartupDatabaseGate(new([new Declaration()], DatabaseProviderIdResolver.Empty),
    new([], DatabaseProviderIdResolver.Empty), new([], DatabaseProviderIdResolver.Empty), new RejectScopeFactory());
StartupDatabasePreparationResult prepared = await gate.PrepareAsync(new(new("CustomDb", "1", "opaque"),
    DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5)), CancellationToken.None);
if (!prepared.Succeeded || !prepared.Skipped || prepared.ErrorCode is not null)
    throw new InvalidOperationException("Unexpected standalone preparation result.");
Console.WriteLine(prepared);

sealed class Declaration : IDatabaseDeploymentCapabilityProvider
{
    public DatabaseDeploymentCapability Capability { get; } = new("CustomDb", DatabaseDeploymentSupport.SingleInstanceOnly);
    public ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target, CancellationToken cancellationToken) =>
        throw new InvalidOperationException("Pure deployment validation must not resolve identity.");
}
sealed class RejectScopeFactory : IServiceScopeFactory
{
    public IServiceScope CreateScope() => throw new InvalidOperationException("Standalone preparation must not create a migration scope.");
}
