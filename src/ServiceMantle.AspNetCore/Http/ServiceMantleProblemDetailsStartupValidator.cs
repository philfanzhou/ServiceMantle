using Microsoft.Extensions.Hosting;

namespace ServiceMantle.Http;

internal sealed class ServiceMantleProblemDetailsStartupValidator(
    ServiceMantleExceptionMappingRegistry mappingRegistry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = mappingRegistry;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
