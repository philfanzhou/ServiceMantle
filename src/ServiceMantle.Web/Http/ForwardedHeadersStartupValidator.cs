using Microsoft.Extensions.Hosting;

namespace ServiceMantle.Web.Http;

internal sealed class ForwardedHeadersStartupValidator(
    ForwardedHeadersSnapshotProvider snapshotProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _ = snapshotProvider.GetRequiredSnapshot();
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
