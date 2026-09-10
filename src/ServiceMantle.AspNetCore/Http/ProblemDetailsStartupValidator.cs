using Microsoft.Extensions.Hosting;

namespace ServiceMantle.AspNetCore.Http;

internal sealed class ProblemDetailsStartupValidator(
    ExceptionMappingRegistry mappingRegistry) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        _ = mappingRegistry;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
