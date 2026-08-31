using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Logging;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantleSensitiveHeaderStartupValidator(
    IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var registry = serviceProvider.GetRequiredService<ServiceMantleSensitiveHeaderRegistry>();
        _ = registry.GetRequiredSnapshot();
        var ownedSanitizer = serviceProvider
            .GetRequiredService<ServiceMantleSensitiveHeaderSanitizer>()
            .Sanitizer;
        var registeredSanitizer = serviceProvider.GetService<StructuredLogSanitizer>();
        if (!ReferenceEquals(ownedSanitizer, registeredSanitizer))
        {
            throw new ServiceMantleSensitiveHeaderConfigurationException(
                WellKnownSensitiveHeaderConfigurationErrorCodes.SanitizerConflict,
                nameof(StructuredLogSanitizer));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
