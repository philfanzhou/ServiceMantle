using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

internal sealed record ServiceMantleManagementApiRegistration(string? RootPath, TimeSpan SnapshotTimeout);

/// <summary>Marks the endpoints that belong to the protected management API v1 group.</summary>
internal sealed record ServiceMantleManagementApiMetadata;

internal sealed class ServiceMantleManagementApiState(
    IEnumerable<ServiceMantleManagementApiRegistration> registrations,
    IServiceProvider services)
{
    private IEndpointRouteBuilder? endpoints;
    private IApplicationBuilder? application;
    private int mapCount;

    /// <summary>
    /// Returns the single normalized versioned root shared by every registration.
    /// </summary>
    internal string GetRootPath()
    {
        string? baseline = null;
        var count = 0;
        foreach (var registration in registrations)
        {
            string normalized;
            try
            {
                // Path normalization, the length limit, the character set and the /health conflict
                // rules stay owned by the phase gate; this entry point only fixes the version.
                normalized = ServiceMantlePhaseGateState.Normalize(registration.RootPath);
            }
            catch (InvalidOperationException)
            {
                throw Failure();
            }

            if (!normalized.EndsWith('/' + ServiceMantleManagementApiDefaults.VersionSegment, StringComparison.Ordinal) ||
                baseline is not null && !string.Equals(baseline, normalized, StringComparison.Ordinal))
            {
                throw Failure();
            }

            baseline = normalized;
            count++;
        }

        return count > 0 ? baseline! : throw Failure();
    }

    internal void RecordMap(IEndpointRouteBuilder routeBuilder)
    {
        endpoints = routeBuilder;
        // Only a builder that is also the application can prove that the composed pipeline ran.
        application = routeBuilder as IApplicationBuilder;
        mapCount++;
    }

    internal async Task ValidateAsync(CancellationToken cancellationToken)
    {
        var root = GetRootPath();
        await ValidateCapabilitiesAsync(cancellationToken).ConfigureAwait(false);
        if (mapCount == 0)
        {
            return;
        }

        if (mapCount != 1 || endpoints is null) throw Failure();
        if (application is null || !ServiceMantlePipelineComposition.IsCompleted(application)) throw MissingCapability();
        foreach (var endpoint in endpoints.DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Where(endpoint => endpoint.Metadata.GetMetadata<ServiceMantleManagementApiMetadata>() is not null))
        {
            Validate(endpoint, root);
        }
    }

    /// <summary>
    /// Rejects a group endpoint whose final metadata weakens the fixed baseline. A stricter
    /// authorization requirement added on top of the baseline policy remains allowed.
    /// </summary>
    private static void Validate(RouteEndpoint endpoint, string root)
    {
        var markers = endpoint.Metadata.GetOrderedMetadata<ServiceMantleManagementSurfaceMetadata>();
        if (markers.Count != 1 || markers[0].Surface != ServiceMantleManagementSurface.Management ||
            !ServiceMantlePhaseGateState.Under(endpoint.RoutePattern.RawText ?? "", root) ||
            endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null ||
            !endpoint.Metadata.OfType<IAuthorizeData>().Any(data => string.Equals(
                data.Policy,
                ManagementAuthorizationDefaults.AdminPolicyName,
                StringComparison.Ordinal)) ||
            endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null ||
            !string.Equals(
                endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName,
                ServiceMantleRateLimitingDefaults.ManagementPolicyName,
                StringComparison.Ordinal) ||
            endpoint.Metadata.GetMetadata<ServiceMantleSecurityResponseHeadersMetadata>() is null)
        {
            throw Failure();
        }
    }

    private async Task ValidateCapabilitiesAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (services.GetService<ServiceMantleSecurityResponseHeadersRegistration>() is null ||
            services.GetService<ServiceMantleRateLimitingSnapshotProvider>() is null ||
            services.GetService<ServiceMantlePhaseGateState>() is null)
        {
            throw MissingCapability();
        }

        var policies = services.GetService<IAuthorizationPolicyProvider>() ?? throw MissingCapability();
        if (await policies.GetPolicyAsync(ManagementAuthorizationDefaults.AdminPolicyName).ConfigureAwait(false) is null)
        {
            throw MissingCapability();
        }

        // The consuming service owns the identity provider and the login flow; the group only needs
        // the default schemes an authentication challenge and a forbid result resolve through.
        var schemes = services.GetService<IAuthenticationSchemeProvider>() ?? throw MissingCapability();
        if (await schemes.GetDefaultAuthenticateSchemeAsync().ConfigureAwait(false) is null ||
            await schemes.GetDefaultChallengeSchemeAsync().ConfigureAwait(false) is null ||
            await schemes.GetDefaultForbidSchemeAsync().ConfigureAwait(false) is null)
        {
            throw MissingCapability();
        }
    }

    internal static InvalidOperationException Failure() =>
        new("The ServiceMantle management API v1 configuration or endpoint mapping is invalid.");

    internal static InvalidOperationException MissingCapability() =>
        new("The ServiceMantle management API v1 requires the composed ServiceMantle pipeline, " +
            "the security response headers, the named rate-limit policies, the management " +
            "authorization policy, and resolvable default authentication schemes.");
}

internal sealed class ServiceMantleManagementApiStartupValidator(ServiceMantleManagementApiState state) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => state.ValidateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
