// Proves that one package reference - ServiceMantle.Serilog - is enough to reach every entry point
// that used to require ServiceMantle.Serilog.GrafanaLoki as a separate package.
//
// It also proves the provider-neutral contract boundary: implementing and registering the remote
// log authorization resolver requires no provider-named using at all. The resolver lives in the
// core package's ServiceMantle.Logging namespace, and both registration entries live in
// Microsoft.Extensions.Hosting / Microsoft.Extensions.DependencyInjection with target-typed
// option lambdas, so this file deliberately contains no using ServiceMantle.Serilog.GrafanaLoki.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Logging;

var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddSingleton<IRemoteLogAuthorizationResolver, ConsumerResolver>();
builder.AddServiceMantleSerilog(options => options.MinimumLevel = "Information");
builder.AddServiceMantleGrafanaLoki(options =>
{
    options.Enabled = true;
    options.Endpoint = new Uri("https://loki.invalid/loki/api/v1/push");
    options.AuthorizationHeaderResolverName = "package-consumer";
});

using var host = builder.Build();
await host.StartAsync();
Console.WriteLine(
    "ServiceMantle.Serilog consumer started; console and Grafana Loki both resolved from one " +
    $"package reference, and the remote sink resolved its authorization header through " +
    $"{typeof(IRemoteLogAuthorizationResolver).FullName} without any provider-named using.");
await host.StopAsync();

// The resolver is the neutral contract: nothing in its declaration or registration names a
// provider or a backend product.
internal sealed class ConsumerResolver : IRemoteLogAuthorizationResolver
{
    public string? ResolveAuthorizationHeader(string name) => "Bearer package-consumer-placeholder";
}
