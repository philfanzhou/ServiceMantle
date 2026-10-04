using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Bootstrap;
using ServiceMantle.Database.Oracle;

var services = new ServiceCollection();
services.AddServiceMantleOracleDeploymentCapability().AddServiceMantleOracleDeploymentCapability();
var provider = new OracleDatabaseDeploymentCapabilityProvider();
var unsupported = await provider.GetCanonicalTargetIdentityAsync(new BootstrapDatabaseConfiguration(
    WellKnownDatabaseProviderIds.Oracle, null, "Server=one,two;Database=app"), default);
if (services.Count != 1 || unsupported.Length != 0 || provider.Capability.Support != DatabaseDeploymentSupport.SingleAndMultiInstance)
    throw new InvalidOperationException("Explicit deployment support did not resolve.");
Console.WriteLine("Oracle deployment package consumption verified.");
