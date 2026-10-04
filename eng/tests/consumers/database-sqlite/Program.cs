using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Sqlite;

var provider = new SqliteDatabaseTargetPreparationProvider(new SqliteTargetRecoveryOptions(enabled: true, recoveryTimeout: TimeSpan.FromSeconds(2)));
var services = new ServiceCollection();
services.AddSingleton<IDatabaseTargetPreparationProvider>(provider);
services.AddSingleton<IDatabaseDeploymentCapabilityProvider>(provider);
var registry = new DatabaseTargetPreparationProviderRegistry([provider], DatabaseProviderIdResolver.Empty);
if (!registry.TryGetProvider(WellKnownDatabaseProviderIds.Sqlite, out var resolved) || !ReferenceEquals(provider, resolved) || new SqliteTargetRecoveryOptions().Enabled)
    throw new InvalidOperationException("Unexpected SQLite recovery registration.");
var result = await provider.ObserveAsync(new(WellKnownDatabaseProviderIds.Sqlite, null, "Data Source=:memory:"), CancellationToken.None);
if (result.ErrorCode != WellKnownDatabaseTargetPreparationErrorCodes.InvalidTarget)
    throw new InvalidOperationException("Unexpected SQLite input validation.");
Console.WriteLine("SQLite recovery package consumption verified.");
