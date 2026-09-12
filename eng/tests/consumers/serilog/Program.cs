// Proves that one package reference - ServiceMantle.Serilog - is enough to reach every entry point
// that used to require ServiceMantle.Serilog.GrafanaLoki as a separate package.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Serilog.GrafanaLoki;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<ILokiAuthorizationHeaderResolver, ConsumerResolver>();
builder.AddServiceMantleSerilog(options => options.MinimumLevel = LogLevel.Information);
builder.AddServiceMantleGrafanaLoki(options =>
{
    options.Enabled = true;
    options.Endpoint = new Uri("https://loki.invalid/loki/api/v1/push");
    options.AuthorizationHeaderResolverName = "package-consumer";
});

using var host = builder.Build();
await host.StartAsync();
Console.WriteLine(
    "ServiceMantle.Serilog consumer started; console and Grafana Loki both resolved from " +
    $"{typeof(GrafanaLokiOptions).Assembly.Location}.");
await host.StopAsync();

// The sink refuses to start without a resolved authorization header, so the consumer has to supply
// one to reach the enabled path at all. The value never leaves this process: nothing is pushed,
// because the endpoint host does not resolve.
internal sealed class ConsumerResolver : ILokiAuthorizationHeaderResolver
{
    public string? ResolveAuthorizationHeader(string name) => "Bearer package-consumer-placeholder";
}
