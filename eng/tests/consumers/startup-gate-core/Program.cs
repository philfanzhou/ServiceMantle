using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;
using ServiceMantle.Health;
using ServiceMantle.Migration;

var services = new ServiceCollection();
services.AddServiceMantleStartupDatabaseGateServices().AddServiceMantleStartupDatabaseGateServices();
using var provider = services.BuildServiceProvider();
_ = provider.GetRequiredService<StartupDatabaseGate>();
if (provider.GetRequiredService<StartupDatabaseReceipt>().State != ServiceMigrationReadinessState.NotStarted ||
    provider.GetService<StartupDatabaseGateOptions>() is not null || services.Any(d => d.ServiceType.Name == "IHostedService"))
    throw new InvalidOperationException("Unexpected startup registration.");
Console.WriteLine("Core-only startup gate registration verified.");

var rootKeyFile = new RootKeyFileSource(Path.Combine(Path.GetTempPath(), "servicemantle-core-private", "root.key"));
Func<string> rootKeyResolver = rootKeyFile.Resolve;
_ = rootKeyResolver; // Construction and delegate binding perform no filesystem I/O.
