using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using OpenTelemetry.Exporter;

namespace ServiceMantle.OpenTelemetry.Prometheus;

internal sealed record PrometheusRegistration(PrometheusOptions Options);

internal sealed record PrometheusSnapshot(
    bool Enabled,
    string EndpointPath,
    string? AuthorizationPolicyName);

internal sealed class PrometheusSnapshotProvider(
    IEnumerable<PrometheusRegistration> registrations)
{
    private readonly object sync = new();
    private PrometheusSnapshot? snapshot;

    internal PrometheusSnapshot GetRequiredSnapshot()
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

            PrometheusSnapshot? baseline = null;
            foreach (var registration in registrations)
            {
                var candidate = Normalize(registration.Options);
                if (baseline is not null && baseline != candidate)
                {
                    throw Failure(
                        WellKnownPrometheusErrorCodes.ConflictingRegistration,
                        "Registration");
                }

                baseline = candidate;
            }

            snapshot = baseline ?? new(false, PrometheusDefaults.EndpointPath, null);
            return snapshot;
        }
    }

    internal bool TryGetMappingSnapshot(out PrometheusSnapshot? mapping)
    {
        try
        {
            mapping = GetRequiredSnapshot();
            return mapping.Enabled;
        }
        catch (PrometheusConfigurationException)
        {
            mapping = null;
            return false;
        }
    }

    private static PrometheusSnapshot Normalize(PrometheusOptions options)
    {
        if (!options.Enabled)
        {
            return new(false, PrometheusDefaults.EndpointPath, null);
        }

        if (!TryNormalizePath(options.EndpointPath, out var path))
        {
            throw Failure(
                WellKnownPrometheusErrorCodes.InvalidEndpointPath,
                nameof(options.EndpointPath));
        }

        if (string.Equals(path, "/management", StringComparison.OrdinalIgnoreCase))
        {
            throw Failure(
                WellKnownPrometheusErrorCodes.EndpointPathConflict,
                nameof(options.EndpointPath));
        }

        if (string.IsNullOrWhiteSpace(options.AuthorizationPolicyName))
        {
            throw Failure(
                WellKnownPrometheusErrorCodes.AuthorizationPolicyRequired,
                nameof(options.AuthorizationPolicyName));
        }

        return new(true, path, options.AuthorizationPolicyName.Trim());
    }

    private static bool TryNormalizePath(string? path, out string normalized)
    {
        normalized = string.Empty;
        if (path is not { Length: > 1 } ||
            path[0] != '/')
        {
            return false;
        }

        normalized = path;
        while (normalized.Contains('%', StringComparison.Ordinal))
        {
            if (!HasValidPercentEncoding(normalized))
            {
                return false;
            }

            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(normalized);
            }
            catch (UriFormatException)
            {
                return false;
            }

            if (string.Equals(decoded, normalized, StringComparison.Ordinal))
            {
                return false;
            }

            normalized = decoded;
        }

        return normalized is { Length: > 1 } &&
            normalized[0] == '/' &&
            normalized[^1] != '/' &&
            normalized.AsSpan(1).IndexOf('/') < 0 &&
            !normalized.Contains('?', StringComparison.Ordinal) &&
            !normalized.Contains('#', StringComparison.Ordinal) &&
            !normalized.Contains('\\', StringComparison.Ordinal) &&
            !normalized.Contains("..", StringComparison.Ordinal) &&
            !normalized.Contains('{', StringComparison.Ordinal) &&
            !normalized.Contains('}', StringComparison.Ordinal) &&
            !normalized.Any(char.IsControl) &&
            !normalized.Any(char.IsWhiteSpace);
    }

    private static bool HasValidPercentEncoding(string value)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '%')
            {
                continue;
            }

            if (index + 2 >= value.Length ||
                !char.IsAsciiHexDigit(value[index + 1]) ||
                !char.IsAsciiHexDigit(value[index + 2]))
            {
                return false;
            }

            index += 2;
        }

        return true;
    }

    private static PrometheusConfigurationException Failure(
        string errorCode,
        string fieldName) => new(errorCode, fieldName);
}

internal sealed class PrometheusExporterOptionsPolicy :
    IPostConfigureOptions<PrometheusAspNetCoreOptions>,
    IValidateOptions<PrometheusAspNetCoreOptions>
{
    public void PostConfigure(string? name, PrometheusAspNetCoreOptions options)
    {
        if (IsDefaultOptionsName(name))
        {
            ApplyFixedValues(options);
        }
    }

    public ValidateOptionsResult Validate(string? name, PrometheusAspNetCoreOptions options)
    {
        if (!IsDefaultOptionsName(name))
        {
            return ValidateOptionsResult.Skip;
        }

        return HasFixedValues(options)
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(
                WellKnownPrometheusErrorCodes.ExporterOptionsConflict);
    }

    private static void ApplyFixedValues(PrometheusAspNetCoreOptions options)
    {
        options.ScrapeEndpointPath = PrometheusDefaults.EndpointPath;
        options.MaxScrapeResponseSizeBytes = PrometheusDefaults.MaximumResponseSizeBytes;
        options.ScopeInfoEnabled = false;
        options.TargetInfoEnabled = false;
        options.ResourceConstantLabels = null;
    }

    private static bool HasFixedValues(PrometheusAspNetCoreOptions options) =>
        string.Equals(
            options.ScrapeEndpointPath,
            PrometheusDefaults.EndpointPath,
            StringComparison.Ordinal) &&
        options.MaxScrapeResponseSizeBytes == PrometheusDefaults.MaximumResponseSizeBytes &&
        !options.ScopeInfoEnabled &&
        !options.TargetInfoEnabled &&
        options.ResourceConstantLabels is null;

    private static bool IsDefaultOptionsName(string? name) =>
        name is null ||
        string.Equals(name, Microsoft.Extensions.Options.Options.DefaultName, StringComparison.Ordinal);
}

internal sealed class PrometheusEndpointState(IHostApplicationLifetime lifetime)
{
    private int mappingCount;
    private IEndpointRouteBuilder? routeBuilder;

    internal SemaphoreSlim ScrapeSlots { get; } = new(
        PrometheusDefaults.MaximumConcurrentScrapes,
        PrometheusDefaults.MaximumConcurrentScrapes);

    internal CancellationToken ApplicationStopping => lifetime.ApplicationStopping;

    internal bool IsStopping => lifetime.ApplicationStopping.IsCancellationRequested;

    internal int MappingCount => Volatile.Read(ref mappingCount);

    internal int AvailableScrapeSlots => ScrapeSlots.CurrentCount;

    internal void RecordMapping(IEndpointRouteBuilder endpoints)
    {
        Interlocked.CompareExchange(ref routeBuilder, endpoints, null);
        Interlocked.Increment(ref mappingCount);
    }

    internal IReadOnlyList<EndpointDataSource> GetEndpointDataSources() =>
        Volatile.Read(ref routeBuilder)?.DataSources.ToArray() ?? [];
}

internal sealed class PrometheusEndpointMetadata;

internal sealed class PrometheusStartupValidator(
    PrometheusSnapshotProvider snapshotProvider,
    PrometheusEndpointState endpointState,
    IServiceProvider serviceProvider) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = snapshotProvider.GetRequiredSnapshot();
        if (!snapshot.Enabled)
        {
            if (endpointState.MappingCount != 0)
            {
                throw Failure(WellKnownPrometheusErrorCodes.EndpointPathConflict, "Endpoint");
            }

            return;
        }

        if (endpointState.MappingCount != 1)
        {
            throw Failure(
                WellKnownPrometheusErrorCodes.EndpointMappingRequired,
                "Endpoint");
        }

        var authorizationPolicyProvider = serviceProvider.GetService(typeof(IAuthorizationPolicyProvider))
            as IAuthorizationPolicyProvider;
        var policy = authorizationPolicyProvider is null
            ? null
            : await authorizationPolicyProvider.GetPolicyAsync(snapshot.AuthorizationPolicyName!);
        if (policy is null)
        {
            throw Failure(
                WellKnownPrometheusErrorCodes.AuthorizationPolicyNotFound,
                nameof(PrometheusOptions.AuthorizationPolicyName));
        }

        var routeEndpoints = endpointState.GetEndpointDataSources()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .ToArray();
        var ownedEndpoints = routeEndpoints
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<PrometheusEndpointMetadata>() is not null)
            .ToArray();
        var conflictingEndpoints = routeEndpoints
            .Where(endpoint =>
                endpoint.Metadata.GetMetadata<PrometheusEndpointMetadata>() is null &&
                string.Equals(
                    endpoint.RoutePattern.RawText,
                    snapshot.EndpointPath,
                    StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (ownedEndpoints.Length != 1 || conflictingEndpoints.Length != 0)
        {
            throw Failure(
                WellKnownPrometheusErrorCodes.EndpointPathConflict,
                nameof(PrometheusOptions.EndpointPath));
        }
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private static PrometheusConfigurationException Failure(
        string errorCode,
        string fieldName) => new(errorCode, fieldName);
}
