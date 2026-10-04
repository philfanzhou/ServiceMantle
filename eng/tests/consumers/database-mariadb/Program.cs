using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MariaDb;

var services = new ServiceCollection();
services.AddServiceMantleMariaDbDeploymentCapability().AddServiceMantleMariaDbDeploymentCapability();
var provider = new MariaDbDatabaseDeploymentCapabilityProvider();
var unsupported = await provider.GetCanonicalTargetIdentityAsync(new BootstrapDatabaseConfiguration(
    WellKnownDatabaseProviderIds.MariaDb, null, "Server=one,two;Database=app"), default);
if (services.Count != 1 || unsupported.Length != 0 || provider.Capability.Support != DatabaseDeploymentSupport.SingleAndMultiInstance)
    throw new InvalidOperationException("Explicit deployment support did not resolve.");
Console.WriteLine("MariaDb deployment package consumption verified.");
