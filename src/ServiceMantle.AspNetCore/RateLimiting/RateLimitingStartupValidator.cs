using Microsoft.Extensions.Hosting;

namespace ServiceMantle.AspNetCore.RateLimiting;

internal sealed class RateLimitingStartupValidator(
    RateLimitingSnapshotProvider snapshotProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        snapshotProvider.GetRequiredSnapshot();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
