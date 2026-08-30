using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantleForwardedHeadersMiddleware
{
    private readonly ForwardedHeadersMiddleware frameworkMiddleware;

    public ServiceMantleForwardedHeadersMiddleware(
        RequestDelegate next,
        ILoggerFactory loggerFactory,
        ServiceMantleForwardedHeadersSnapshotProvider snapshotProvider)
    {
        frameworkMiddleware = new ForwardedHeadersMiddleware(
            next,
            loggerFactory,
            Options.Create(snapshotProvider.GetRequiredSnapshot()));
    }

    public Task Invoke(HttpContext context) => frameworkMiddleware.Invoke(context);
}
