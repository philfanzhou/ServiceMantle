using ServiceMantle.AspNetCore.Http;

namespace Microsoft.AspNetCore.Builder;

/// <summary>Marks endpoints that require the ServiceMantle security response-header baseline.</summary>
public static class ServiceMantleSecurityResponseHeadersEndpointConventionBuilderExtensions
{
    /// <summary>Adds the immutable security response-header marker exactly once.</summary>
    public static TBuilder RequireServiceMantleSecurityResponseHeaders<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Add(endpointBuilder =>
        {
            if (!endpointBuilder.Metadata.OfType<SecurityResponseHeadersMetadata>().Any())
            {
                endpointBuilder.Metadata.Add(new SecurityResponseHeadersMetadata());
            }
        });
        return builder;
    }
}
