using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Records the opt-in management entries a host mapped and validates their fixed baseline before
/// the host starts.
/// </summary>
internal sealed class ServiceMantleManagementEntryState(IServiceProvider services)
{
    private readonly List<IEndpointRouteBuilder> builders = [];

    internal string GetRootPath() =>
        (services.GetService<ServiceMantleManagementApiState>() ?? throw MissingCapability())
            .GetRootPath();

    internal void RecordMap(IEndpointRouteBuilder routeBuilder)
    {
        if (!builders.Contains(routeBuilder))
        {
            builders.Add(routeBuilder);
        }
    }

    internal async Task ValidateAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (builders.Count == 0)
        {
            return;
        }

        var root = GetRootPath();
        var endpoints = Collect();
        if (endpoints.Count == 0)
        {
            throw Failure();
        }

        var seen = new HashSet<ServiceMantleManagementEntryKind>();
        var validated = new List<ValidatedEntry>();
        foreach (var endpoint in endpoints)
        {
            var definition = Validate(endpoint, root);
            if (!seen.Add(definition.Kind))
            {
                throw Failure();
            }

            validated.Add(new ValidatedEntry(endpoint, definition));
        }

        await ValidateCapabilitiesAsync(validated, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Collects every recorded endpoint, rejecting a builder that is not the application whose
    /// ServiceMantle pipeline already ran. Entries live beside the protected group, never inside it.
    /// </summary>
    private List<RouteEndpoint> Collect()
    {
        var collected = new List<RouteEndpoint>();
        foreach (var builder in builders)
        {
            if (builder is not IApplicationBuilder application ||
                !ServiceMantlePipelineComposition.IsCompleted(application))
            {
                throw MissingCapability();
            }

            foreach (var endpoint in builder.DataSources
                .SelectMany(source => source.Endpoints)
                .OfType<RouteEndpoint>()
                .Where(endpoint =>
                    endpoint.Metadata.GetOrderedMetadata<ServiceMantleManagementEntryMetadata>().Count > 0))
            {
                if (!collected.Contains(endpoint))
                {
                    collected.Add(endpoint);
                }
            }
        }

        return collected;
    }

    /// <summary>
    /// Rejects an entry whose path, method, surface, authentication, rate limiting, security
    /// headers, or unsafe-request guard does not match its fixed definition exactly.
    /// </summary>
    private static ServiceMantleManagementEntryDefinition Validate(RouteEndpoint endpoint, string root)
    {
        var markers = endpoint.Metadata.GetOrderedMetadata<ServiceMantleManagementEntryMetadata>();
        if (markers.Count != 1 || !Enum.IsDefined(markers[0].Kind))
        {
            throw Failure();
        }

        var definition = ServiceMantleManagementEntryDefaults.Get(markers[0].Kind);
        var surfaces = endpoint.Metadata.GetOrderedMetadata<ServiceMantleManagementSurfaceMetadata>();
        var methods = endpoint.Metadata.GetMetadata<HttpMethodMetadata>()?.HttpMethods;
        if (endpoint.Metadata.GetMetadata<ServiceMantleManagementApiMetadata>() is not null ||
            !string.Equals(
                (endpoint.RoutePattern.RawText ?? string.Empty).TrimEnd('/'),
                root + definition.PathSuffix,
                StringComparison.OrdinalIgnoreCase) ||
            surfaces.Count != 1 ||
            surfaces[0].Surface != definition.Surface ||
            methods is null ||
            !methods.OrderBy(method => method, StringComparer.OrdinalIgnoreCase).SequenceEqual(
                definition.Methods.OrderBy(method => method, StringComparer.OrdinalIgnoreCase),
                StringComparer.OrdinalIgnoreCase) ||
            endpoint.Metadata.GetMetadata<DisableRateLimitingAttribute>() is not null ||
            !string.Equals(
                endpoint.Metadata.GetMetadata<EnableRateLimitingAttribute>()?.PolicyName,
                definition.RateLimitPolicyName,
                StringComparison.Ordinal) ||
            endpoint.Metadata.GetMetadata<ServiceMantleSecurityResponseHeadersMetadata>() is null ||
            definition.RequiresUnsafeRequestHeader !=
                (endpoint.Metadata.GetMetadata<ServiceMantleUnsafeRequestGuardMetadata>() is not null))
        {
            throw Failure();
        }

        var anonymous = endpoint.Metadata.GetMetadata<IAllowAnonymous>() is not null;
        if (definition.IsAnonymous)
        {
            // The anonymous entries are anonymous by contract, not by a host-supplied override, so a
            // silently dropped AllowAnonymous is as invalid as an added one.
            if (!anonymous)
            {
                throw Failure();
            }
        }
        else if (anonymous ||
            !definition.AuthorizationPolicyNames.All(policyName =>
                endpoint.Metadata.OfType<IAuthorizeData>().Any(data => string.Equals(
                    data.Policy,
                    policyName,
                    StringComparison.Ordinal))))
        {
            throw Failure();
        }

        return definition;
    }

    private async Task ValidateCapabilitiesAsync(
        IReadOnlyList<ValidatedEntry> validated,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (services.GetService<ServiceMantleSecurityResponseHeadersRegistration>() is null ||
            services.GetService<ServiceMantleRateLimitingSnapshotProvider>() is null ||
            services.GetService<ServiceMantlePhaseGateState>() is null)
        {
            throw MissingCapability();
        }

        var protectedEntries = validated.Where(entry => !entry.Definition.IsAnonymous).ToArray();
        if (protectedEntries.Length == 0)
        {
            return;
        }

        var policies = services.GetService<IAuthorizationPolicyProvider>() ?? throw MissingCapability();
        foreach (var policyName in protectedEntries
            .SelectMany(entry => entry.Definition.AuthorizationPolicyNames)
            .Distinct(StringComparer.Ordinal))
        {
            if (await policies.GetPolicyAsync(policyName).ConfigureAwait(false) is null)
            {
                throw MissingCapability();
            }
        }

        var schemes = services.GetService<IAuthenticationSchemeProvider>() ?? throw MissingCapability();
        if (await schemes.GetDefaultAuthenticateSchemeAsync().ConfigureAwait(false) is null ||
            await schemes.GetDefaultChallengeSchemeAsync().ConfigureAwait(false) is null ||
            await schemes.GetDefaultForbidSchemeAsync().ConfigureAwait(false) is null)
        {
            throw MissingCapability();
        }

        // The entries that pin the fixed management cookie scheme need that handler to exist.
        if (protectedEntries.Any(entry => entry.Definition.AuthorizationPolicyNames.Contains(
                ManagementAuthorizationDefaults.SessionPolicyName,
                StringComparer.Ordinal)) &&
            await schemes.GetSchemeAsync(
                ServiceMantleManagementSessionDefaults.AuthenticationScheme).ConfigureAwait(false) is null)
        {
            throw MissingCapability();
        }

        foreach (var entry in protectedEntries.Where(
            entry => entry.Definition.RequiredSchemePolicyName is not null))
        {
            await ValidatePinnedSchemeAsync(entry, policies).ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Rejects an entry whose effective authentication scheme set is not exactly the fixed
    /// management cookie scheme.
    /// </summary>
    /// <remarks>
    /// The set is read from the endpoint's combined authorization policy rather than from the two
    /// policy names the mapping applied, because a handler may add authorization data of its own.
    /// Anything that widens the set - a scheme named directly on the endpoint, or a further policy
    /// that names one - is refused here. A further policy that only adds requirements names no
    /// scheme, leaves the set unchanged, and is a stricter rule rather than a downgrade, so it is
    /// admitted.
    /// </remarks>
    private static async Task ValidatePinnedSchemeAsync(
        ValidatedEntry entry,
        IAuthorizationPolicyProvider policies)
    {
        var combined = await AuthorizationPolicy.CombineAsync(
            policies,
            entry.Endpoint.Metadata.OfType<IAuthorizeData>()).ConfigureAwait(false);
        if (combined is null ||
            combined.AuthenticationSchemes.Count != 1 ||
            !string.Equals(
                combined.AuthenticationSchemes[0],
                ServiceMantleManagementSessionDefaults.AuthenticationScheme,
                StringComparison.Ordinal))
        {
            throw Failure();
        }
    }

    private sealed record ValidatedEntry(
        RouteEndpoint Endpoint,
        ServiceMantleManagementEntryDefinition Definition);

    internal static InvalidOperationException Failure() =>
        new("The ServiceMantle management entry mapping is invalid.");

    internal static InvalidOperationException MissingCapability() =>
        new("The ServiceMantle management entries require AddServiceMantleManagementApiV1, " +
            "AddServiceMantleManagementEntries, the composed ServiceMantle pipeline, the security " +
            "response headers, the named rate-limit policies, and the authorization policies and " +
            "authentication schemes their protected entries resolve through.");
}

internal sealed class ServiceMantleManagementEntryStartupValidator(
    ServiceMantleManagementEntryState state) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken) => state.ValidateAsync(cancellationToken);

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
