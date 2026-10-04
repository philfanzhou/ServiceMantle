using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection.Extensions;
using ServiceMantle.Web.Http;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Explicitly attaches Correlation ID propagation to a selected HttpClient.</summary>
public static class ServiceMantleCorrelationIdHttpClientBuilderExtensions
{
    /// <summary>Attaches propagation once for this named client, independently of host identity registration.</summary>
    /// <remarks>Only resolved middleware slots are propagated. Restrict this client and its redirects to trusted destinations.</remarks>
    public static IHttpClientBuilder AddServiceMantleCorrelationIdPropagation(this IHttpClientBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        if (builder.Services.Any(d => d.ServiceType == typeof(Registration) &&
            d.ImplementationInstance is Registration registration && registration.Name == builder.Name)) return builder;
        builder.Services.TryAddSingleton<IHttpContextAccessor, HttpContextAccessor>();
        builder.Services.TryAddTransient<CorrelationIdPropagationHandler>();
        builder.Services.AddSingleton(new Registration(builder.Name));
        builder.AddHttpMessageHandler<CorrelationIdPropagationHandler>();
        return builder;
    }

    private sealed record Registration(string Name);
}
