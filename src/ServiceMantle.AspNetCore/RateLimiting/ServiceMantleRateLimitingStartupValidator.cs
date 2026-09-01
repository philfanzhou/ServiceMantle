using Microsoft.Extensions.Hosting;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantleRateLimitingStartupValidator(
    ServiceMantleRateLimitingSnapshotProvider snapshotProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        snapshotProvider.GetRequiredSnapshot();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
