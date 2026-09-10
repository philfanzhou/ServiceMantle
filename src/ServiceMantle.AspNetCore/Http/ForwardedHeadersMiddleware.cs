using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace ServiceMantle.AspNetCore.Http;

internal sealed class ForwardedHeadersMiddleware
{
    private readonly Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware frameworkMiddleware;

    public ForwardedHeadersMiddleware(
        RequestDelegate next,
        ILoggerFactory loggerFactory,
        ForwardedHeadersSnapshotProvider snapshotProvider)
    {
        frameworkMiddleware = new Microsoft.AspNetCore.HttpOverrides.ForwardedHeadersMiddleware(
            next,
            loggerFactory,
            Options.Create(snapshotProvider.GetRequiredSnapshot()));
    }

    public Task Invoke(HttpContext context) => frameworkMiddleware.Invoke(context);
}
