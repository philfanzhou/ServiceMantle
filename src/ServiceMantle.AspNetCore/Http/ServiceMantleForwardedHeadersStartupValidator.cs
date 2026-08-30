using Microsoft.Extensions.Hosting;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantleForwardedHeadersStartupValidator(
    ServiceMantleForwardedHeadersSnapshotProvider snapshotProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = snapshotProvider.GetRequiredSnapshot();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
