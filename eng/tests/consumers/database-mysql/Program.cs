using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.MySql;

var services = new ServiceCollection();
services.AddServiceMantleMySqlDeploymentCapability().AddServiceMantleMySqlDeploymentCapability();
var provider = new MySqlDatabaseDeploymentCapabilityProvider();
var unsupported = await provider.GetCanonicalTargetIdentityAsync(new BootstrapDatabaseConfiguration(
    WellKnownDatabaseProviderIds.MySql, null, "Server=one,two;Database=app"), default);
if (services.Count != 1 || unsupported.Length != 0 || provider.Capability.Support != DatabaseDeploymentSupport.SingleAndMultiInstance)
    throw new InvalidOperationException("Explicit deployment support did not resolve.");
Console.WriteLine("MySql deployment package consumption verified.");
