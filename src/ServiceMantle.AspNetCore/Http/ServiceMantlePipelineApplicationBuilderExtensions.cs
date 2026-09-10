using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.AspNetCore;
using ServiceMantle.AspNetCore.Http;
using ServiceMantle.AspNetCore.Logging;
using ServiceMantle.AspNetCore.PhaseGate;
using ServiceMantle.AspNetCore.RateLimiting;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Composes the ServiceMantle HTTP middleware in its supported relative order.</summary>
public static class ServiceMantlePipelineApplicationBuilderExtensions
{
    /// <summary>
    /// Composes configured forwarding, correlation, Problem Details, routing, security headers,
    /// the phase gate, optional authentication, rate limiting, and optional authorization.
    /// </summary>
    /// <remarks>
    /// Register AddServiceMantle, AddSecurityResponseHeaders, AddSensitiveHeaders, AddRateLimiting,
    /// and AddServiceMantlePhaseGate first. Forwarding remains disabled without AddForwardedHeaders.
    /// Call once before consumer middleware or endpoints; do not also insert the constituent
    /// middleware. Endpoint security-header, rate-limit, and authorization metadata remain explicit.
    /// This does not register authentication, logging hosts, health endpoints, or telemetry, or
    /// rewrite the existing phase-gate and authentication response formats. Sensitive Header
    /// diagnostics must use RequestHeaderDiagnosticProjector explicitly.
    /// </remarks>
    /// <exception cref="InvalidOperationException">
    /// A required capability is absent, the composition was already used, or constituent
    /// ServiceMantle middleware was inserted separately on this builder.
    /// </exception>
    public static WebApplication UseServiceMantlePipeline(this WebApplication app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var services = app.Services;
        if (services.GetService<HostRegistration>() is null ||
            services.GetService<SecurityResponseHeadersRegistration>() is null ||
            services.GetService<SensitiveHeaderRegistry>() is null ||
            services.GetService<RateLimitingSnapshotProvider>() is null ||
            services.GetService<PhaseGateState>() is null)
        {
            throw new InvalidOperationException("The ServiceMantle pipeline requires all HTTP capabilities to be registered.");
        }

        PipelineComposition.Begin(app);
        if (services.GetService<ForwardedHeadersSnapshotProvider>() is not null)
            app.UseServiceMantleForwardedHeaders();
        app.UseServiceMantleCorrelationId();
        app.UseServiceMantleProblemDetails();
        app.UseRouting();
        app.UseServiceMantleSecurityResponseHeaders();
        app.UseServiceMantlePhaseGate();
        if (services.GetService<IAuthenticationSchemeProvider>() is not null)
            app.UseAuthentication();
        app.UseRateLimiter();
        if (services.GetService<IAuthorizationPolicyProvider>() is not null)
            app.UseAuthorization();
        PipelineComposition.Complete(app);
        return app;
    }
}
