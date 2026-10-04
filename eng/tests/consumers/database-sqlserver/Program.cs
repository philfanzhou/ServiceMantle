using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.SqlServer;

var services = new ServiceCollection();
services.AddServiceMantleSqlServerDeploymentCapability().AddServiceMantleSqlServerDeploymentCapability();
var provider = new SqlServerDatabaseDeploymentCapabilityProvider();
var unsupported = await provider.GetCanonicalTargetIdentityAsync(new BootstrapDatabaseConfiguration(
    WellKnownDatabaseProviderIds.SqlServer, null, "Server=one,two;Database=app"), default);
if (services.Count != 1 || unsupported.Length != 0 || provider.Capability.Support != DatabaseDeploymentSupport.SingleAndMultiInstance)
    throw new InvalidOperationException("Explicit deployment support did not resolve.");
Console.WriteLine("SqlServer deployment package consumption verified.");
