using Microsoft.AspNetCore.Http;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantleSecurityResponseHeadersMiddleware(RequestDelegate next)
{
    private static readonly KeyValuePair<string, string>[] MandatoryHeaders =
    [
        new("Cache-Control", "no-store"),
        new("Pragma", "no-cache"),
        new("X-Content-Type-Options", "nosniff"),
        new("X-Frame-Options", "DENY"),
        new("Referrer-Policy", "no-referrer"),
        new(
            "Content-Security-Policy",
            "default-src 'none'; frame-ancestors 'none'; base-uri 'none'; form-action 'none'"),
    ];

    public Task Invoke(HttpContext context)
    {
        var endpoint = context.GetEndpoint();
        if (context.Response.HasStarted ||
            endpoint?.Metadata.GetMetadata<ServiceMantleSecurityResponseHeadersMetadata>() is null)
        {
            return next(context);
        }

        context.Response.OnStarting(static state =>
        {
            var response = (HttpResponse)state;
            foreach (var (name, value) in MandatoryHeaders)
            {
                response.Headers[name] = value;
            }

            return Task.CompletedTask;
        }, context.Response);
        return next(context);
    }
}
