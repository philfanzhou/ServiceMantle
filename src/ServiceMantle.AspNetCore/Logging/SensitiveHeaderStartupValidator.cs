using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Logging;

namespace ServiceMantle.AspNetCore.Logging;

internal sealed class SensitiveHeaderStartupValidator(
    IServiceProvider serviceProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var registry = serviceProvider.GetRequiredService<SensitiveHeaderRegistry>();
        _ = registry.GetRequiredSnapshot();
        var ownedSanitizer = serviceProvider
            .GetRequiredService<SensitiveHeaderSanitizer>()
            .Sanitizer;
        var registeredSanitizer = serviceProvider.GetService<StructuredLogSanitizer>();
        if (!ReferenceEquals(ownedSanitizer, registeredSanitizer))
        {
            throw new SensitiveHeaderConfigurationException(
                WellKnownSensitiveHeaderConfigurationErrorCodes.SanitizerConflict,
                nameof(StructuredLogSanitizer));
        }

        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
