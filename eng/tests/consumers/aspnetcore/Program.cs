// The shape a consuming service starts from: one reference to ServiceMantle.AspNetCore, the service
// identity registered, and a host that starts. It exists to prove the published package is usable
// on its own, so it deliberately enables no optional capability.
using ServiceMantle;
using ServiceMantle.AspNetCore;

var bootstrapDirectory = Directory.CreateTempSubdirectory("servicemantle-consumer");
try
{
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddServiceMantle(
        ServiceId.Parse("package-consumer"),
        InstanceId.Parse("package-consumer-01"),
        bootstrapFilePath: Path.Combine(bootstrapDirectory.FullName, "bootstrap.json"),
        serviceVersion: "1.0.0");

    var application = builder.Build();
    await application.StartAsync();
    Console.WriteLine(
        "ServiceMantle.AspNetCore consumer started against " +
        $"{typeof(ServiceMantleBuilder).Assembly.Location}.");
    await application.StopAsync();
}
finally
{
    bootstrapDirectory.Delete(recursive: true);
}
