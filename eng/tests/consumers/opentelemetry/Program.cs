// Proves that one package reference - ServiceMantle.OpenTelemetry - is enough to reach every
// entry point that used to require ServiceMantle.OpenTelemetry.Otlp and
// ServiceMantle.OpenTelemetry.Prometheus as separate packages.
//
// It also proves the provider-neutral contract boundary: implementing and registering the remote
// telemetry authentication resolver requires no provider-named using at all. The resolver
// contracts live in the core package's ServiceMantle.Diagnostics namespace, the registration
// entry lives in Microsoft.Extensions.DependencyInjection, and the OTLP settings are shaped
// through a target-typed lambda, so this file deliberately contains no
// using ServiceMantle.OpenTelemetry.Otlp. The OTLP configuration types themselves remain
// provider-specific; this file asserts nothing about naming them.
using Microsoft.AspNetCore.Authorization;
using ServiceMantle;
using ServiceMantle.Diagnostics;

var bootstrapDirectory = Directory.CreateTempSubdirectory("servicemantle-consumer");
try
{
    var builder = WebApplication.CreateSlimBuilder(args);
    builder.Logging.ClearProviders();
    builder.Services.AddAuthorization(options => options.AddPolicy(
        "metrics-scrape",
        policy => policy.RequireAssertion(_ => true)));

    builder.Services.AddSingleton<IRemoteTelemetryAuthenticationResolver>(
        new FixedHeaderResolver("trace-auth", "consumer-trace-secret"));

    var serviceMantle = builder.Services.AddServiceMantle(
        ServiceId.Parse("package-consumer"),
        InstanceId.Parse("package-consumer-01"),
        bootstrapFilePath: Path.Combine(bootstrapDirectory.FullName, "bootstrap.json"),
        serviceVersion: "1.0.0");

    serviceMantle.AddOpenTelemetryInstrumentation();
    serviceMantle.AddOpenTelemetryOtlpExporter(options =>
    {
        options.Traces.Enabled = true;
        options.Traces.Endpoint = new Uri("https://collector.invalid:4317/");
        options.Traces.AuthenticationHeaderName = "trace-auth";
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
        "resolved from one package reference, and the trace exporter resolved its authentication " +
        $"header through {typeof(IRemoteTelemetryAuthenticationResolver).FullName} without any " +
        "provider-named using.");
    await application.StopAsync();
}
finally
{
    bootstrapDirectory.Delete(recursive: true);
}

// The resolver is the neutral contract: nothing in its declaration or registration names a
// provider or a backend product.
sealed class FixedHeaderResolver(string resolvedName, string resolvedValue)
    : IRemoteTelemetryAuthenticationResolver
{
    public bool TryResolve(string name, out RemoteTelemetryAuthenticationHeader? header)
    {
        if (name != resolvedName)
        {
            header = null;
            return false;
        }

        header = new RemoteTelemetryAuthenticationHeader(resolvedName, resolvedValue);
        return true;
    }
}
