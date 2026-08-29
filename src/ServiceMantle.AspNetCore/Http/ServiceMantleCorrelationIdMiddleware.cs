using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using ServiceMantle.Logging;

namespace ServiceMantle.Http;

/// <summary>
/// Resolves the Correlation ID exactly once per request and publishes the same value to the request
/// slot, the response header, and the logging scope.
/// </summary>
internal sealed class ServiceMantleCorrelationIdMiddleware
{
    internal const string LoggerCategory = "ServiceMantle.Http.CorrelationId";

    private readonly RequestDelegate next;
    private readonly ServiceLogContext logContext;
    private readonly ILogger logger;

    public ServiceMantleCorrelationIdMiddleware(
        RequestDelegate next,
        ServiceLogContext logContext,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(logContext);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.next = next;
        this.logContext = logContext;
        logger = loggerFactory.CreateLogger(LoggerCategory);
    }

    /// <summary>
    /// Runs the downstream pipeline inside the request Correlation ID scope.
    /// </summary>
    /// <remarks>
    /// The middleware logs nothing itself, never records the raw request header, and never swallows
    /// downstream exceptions or cancellation. The request headers are left untouched: the caller's
    /// unvalidated value stays caller input, while the request slot, the log scope, and the response
    /// header only ever carry the resolved value.
    /// </remarks>
    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        var correlationId = CorrelationIdValue.Resolve(context.Request.Headers);
        CorrelationIdRequestSlot.Set(context, correlationId);

        var response = context.Response;
        if (!response.HasStarted)
        {
            response.OnStarting(
                static state =>
                {
                    var (startedResponse, startedCorrelationId) = ((HttpResponse, string))state;
                    startedResponse.Headers[ServiceMantleHeaderNames.CorrelationId] =
                        startedCorrelationId;
                    return Task.CompletedTask;
                },
                (response, correlationId));
        }

        using (logContext.BeginRequestScope(logger, correlationId))
        {
            await next(context).ConfigureAwait(false);
        }
    }
}
