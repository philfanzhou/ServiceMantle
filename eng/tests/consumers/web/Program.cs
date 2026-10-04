// The shape a consuming service starts from: one reference to ServiceMantle.Web, the service
// identity registered, and a host that starts. It exists to prove the published package is usable
// on its own, so it deliberately enables no optional capability.
using ServiceMantle;
using ServiceMantle.Web;
using ServiceMantle.Migration;
using ServiceMantle.Bootstrap;
using ServiceMantle.Web.Hosting;
using ServiceMantle.Web.Http;

var bootstrapDirectory = Directory.CreateTempSubdirectory("servicemantle-consumer");
try
{
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Services.AddHttpClient("trusted").AddServiceMantleCorrelationIdPropagation().AddServiceMantleCorrelationIdPropagation();
    if (!typeof(CorrelationIdPropagationHandler).IsSubclassOf(typeof(DelegatingHandler)))
        throw new InvalidOperationException("Correlation ID propagation handler did not resolve.");
    builder.Logging.ClearProviders();
    builder.Services.AddServiceMantle(
        ServiceId.Parse("package-consumer"),
        InstanceId.Parse("package-consumer-01"),
        bootstrapFilePath: Path.Combine(bootstrapDirectory.FullName, "bootstrap.json"),
        serviceVersion: "1.0.0").AddServiceMantleStartupDatabaseGateServices();
    // Verify composition on a separate collection; the live sample host remains opt-in.
    var composedBuilder = WebApplication.CreateSlimBuilder(args);
    var composed = composedBuilder.Services;
    composed.AddServiceMantle(ServiceId.Parse("gate-consumer"), InstanceId.Parse("gate-consumer-01"))
        .AddStartupDatabaseGate(new StartupDatabaseGateOptions(new BootstrapDatabaseConfiguration("CustomDb", "1", "opaque"), DatabaseDeploymentMode.SingleInstance, TimeSpan.FromSeconds(5)))
        .AddServiceMantleStartupDatabaseGateServices();
    await using var composedApplication = composedBuilder.Build();
    var composedProvider = composedApplication.Services;
    if (composedProvider.GetServices<StartupDatabaseGate>().Count() != 1 ||
        composedProvider.GetServices<IHostedService>().OfType<StartupDatabaseGateHostedService>().Count() != 1)
        throw new InvalidOperationException("Unexpected composed startup registration.");

    var application = builder.Build();
    await application.StartAsync();
    Console.WriteLine(
        "ServiceMantle.Web consumer started against " +
        $"{typeof(ServiceMantleBuilder).Assembly.Location}.");
    await application.StopAsync();
}
finally
{
    bootstrapDirectory.Delete(recursive: true);
}
