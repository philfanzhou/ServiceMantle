using ServiceMantle.Serilog;

namespace ServiceMantle.Serilog.GrafanaLoki;

internal sealed record ServiceMantleGrafanaLokiRegistration(ServiceMantleGrafanaLokiOptions Options);

internal sealed record ServiceMantleGrafanaLokiConfiguration(
    bool Enabled,
    Uri? Endpoint,
    string? AuthorizationHeaderResolverName,
    int BatchSize,
    int QueueLimit,
    TimeSpan FlushPeriod,
    TimeSpan ShutdownDrainTimeout);

internal sealed class ServiceMantleGrafanaLokiConfigurationProvider(
    IEnumerable<ServiceMantleGrafanaLokiRegistration> registrations)
{
    private readonly object sync = new();
    private ServiceMantleGrafanaLokiConfiguration? snapshot;

    internal ServiceMantleGrafanaLokiConfiguration GetRequiredConfiguration()
    {
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (sync)
        {
            if (snapshot is not null)
            {
                return snapshot;
            }

            ServiceMantleGrafanaLokiConfiguration? baseline = null;
            foreach (var registration in registrations)
            {
                var candidate = Normalize(registration.Options);
                if (baseline is not null && baseline != candidate)
                {
                    throw Failure("Registration", WellKnownServiceMantleGrafanaLokiErrorCodes.ConflictingRegistration);
                }

                baseline = candidate;
            }

            snapshot = baseline ?? DisabledConfiguration();
            return snapshot;
        }
    }

    private static ServiceMantleGrafanaLokiConfiguration Normalize(
        ServiceMantleGrafanaLokiOptions options)
    {
        if (!options.Enabled)
        {
            return DisabledConfiguration();
        }

        var endpoint = options.Endpoint;
        if (endpoint is null ||
            !endpoint.IsAbsoluteUri ||
            !string.IsNullOrEmpty(endpoint.UserInfo) ||
            !string.IsNullOrEmpty(endpoint.Query) ||
            !string.IsNullOrEmpty(endpoint.Fragment) ||
            (endpoint.Scheme != Uri.UriSchemeHttps &&
                !(options.AllowInsecureLoopbackForTesting &&
                  endpoint.Scheme == Uri.UriSchemeHttp &&
                  endpoint.IsLoopback)))
        {
            throw Failure(nameof(options.Endpoint), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidEndpoint);
        }

        var resolverName = options.AuthorizationHeaderResolverName?.Trim();
        if (resolverName is not { Length: >= 1 and <= 128 } ||
            resolverName.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-')))
        {
            throw Failure(
                nameof(options.AuthorizationHeaderResolverName),
                WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidAuthorizationResolverName);
        }

        if (options.BatchSize is < 1 or > 1_000)
        {
            throw Failure(nameof(options.BatchSize), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting);
        }

        if (options.QueueLimit is < 100 or > 50_000)
        {
            throw Failure(nameof(options.QueueLimit), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting);
        }

        if (options.FlushPeriod < TimeSpan.FromSeconds(1) ||
            options.FlushPeriod > TimeSpan.FromSeconds(30))
        {
            throw Failure(nameof(options.FlushPeriod), WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting);
        }

        if (options.ShutdownDrainTimeout < TimeSpan.FromSeconds(1) ||
            options.ShutdownDrainTimeout > TimeSpan.FromSeconds(30))
        {
            throw Failure(
                nameof(options.ShutdownDrainTimeout),
                WellKnownServiceMantleGrafanaLokiErrorCodes.InvalidBoundedSetting);
        }

        return new(
            true,
            new Uri(endpoint.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped)),
            resolverName,
            options.BatchSize,
            options.QueueLimit,
            options.FlushPeriod,
            options.ShutdownDrainTimeout);
    }

    private static ServiceMantleGrafanaLokiConfiguration DisabledConfiguration() => new(
        false,
        null,
        null,
        ServiceMantleGrafanaLokiDefaults.BatchSize,
        ServiceMantleGrafanaLokiDefaults.QueueLimit,
        ServiceMantleGrafanaLokiDefaults.FlushPeriod,
        ServiceMantleGrafanaLokiDefaults.ShutdownDrainTimeout);

    internal static ServiceMantleSerilogConfigurationException Failure(
        string fieldName,
        string errorCode) => new(fieldName, errorCode);
}
