// Proves that one package reference - ServiceMantle.OpenTelemetry - is enough to reach every
// entry point that used to require ServiceMantle.OpenTelemetry.Otlp and
// ServiceMantle.OpenTelemetry.Prometheus as separate packages.
using Microsoft.AspNetCore.Authorization;
using ServiceMantle;
using ServiceMantle.AspNetCore;
using ServiceMantle.OpenTelemetry.Otlp;

var bootstrapDirectory = Directory.CreateTempSubdirectory("servicemantle-consumer");
try
{
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddAuthorization(options => options.AddPolicy(
        "metrics-scrape",
        policy => policy.RequireAssertion(_ => true)));

    var serviceMantle = builder.Services.AddServiceMantle(
        ServiceId.Parse("package-consumer"),
        InstanceId.Parse("package-consumer-01"),
        bootstrapFilePath: Path.Combine(bootstrapDirectory.FullName, "bootstrap.json"),
        serviceVersion: "1.0.0");

    serviceMantle.AddOpenTelemetryInstrumentation();
    serviceMantle.AddOpenTelemetryOtlpExporter(options =>
    {
        options.Traces.Enabled = true;
        options.Traces.Protocol = ServiceMantleOtlpProtocol.Grpc;
        options.Traces.Endpoint = new Uri("https://collector.invalid:4317/");
    });
    serviceMantle.AddOpenTelemetryPrometheusEndpoint(options =>
    {
        options.Enabled = true;
        options.EndpointPath = "/metrics";
        options.AuthorizationPolicyName = "metrics-scrape";
    });

    var application = builder.Build();
    application.UseAuthorization();
    application.MapServiceMantlePrometheusEndpoint();

    await application.StartAsync();
    Console.WriteLine(
        "ServiceMantle.OpenTelemetry consumer started; instrumentation, OTLP, and Prometheus all " +
        $"resolved from {typeof(ServiceMantleOtlpOptions).Assembly.Location}.");
    await application.StopAsync();
}
finally
{
    bootstrapDirectory.Delete(recursive: true);
}
