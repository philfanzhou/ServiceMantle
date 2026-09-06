using Microsoft.AspNetCore.Routing;
using ServiceMantle.AspNetCore;

namespace ServiceMantle.ReferenceService.Logging;

/// <summary>
/// Writes one fixed, sanitized log line per request. It never records the raw path, query, body,
/// Header values, connection settings, or exception detail, and it never swallows cancellation.
/// </summary>
internal sealed class ReferenceRequestLoggingMiddleware
{
    private const string Cancelled = "cancelled";
    private const string Faulted = "faulted";
    private const string Unmatched = "(unmatched)";
    private const string Unknown = "(unknown)";

    private readonly RequestDelegate next;
    private readonly ServiceMantleRequestHeaderDiagnosticProjector projector;
    private readonly ILogger logger;

    public ReferenceRequestLoggingMiddleware(
        RequestDelegate next,
        ServiceMantleRequestHeaderDiagnosticProjector projector,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(projector);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        this.next = next;
        this.projector = projector;
        logger = loggerFactory.CreateLogger(ReferenceLoggingDefaults.RequestLoggerCategory);
    }

    public async Task InvokeAsync(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        var headers = projector.Project(context.Request.Headers);
        var method = context.Request.Method;
        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (context.RequestAborted.IsCancellationRequested)
        {
            // Caller cancellation stays cancellation: it is neither reported as success nor as an
            // internal failure, and it is rethrown unchanged.
            Write(Cancelled, method, context, statusCode: 0, headers);
            throw;
        }
        catch
        {
            Write(Faulted, method, context, statusCode: 0, headers);
            throw;
        }

        Write(Classify(context.Response.StatusCode), method, context, context.Response.StatusCode, headers);
    }

    private void Write(
        string result,
        string method,
        HttpContext context,
        int statusCode,
        IReadOnlyDictionary<string, object?> headers) => logger.LogInformation(
            ReferenceLoggingDefaults.RequestMessageTemplate,
            result,
            method,
            Route(context),
            statusCode,
            headers);

    private static string Route(HttpContext context) => context.GetEndpoint() switch
    {
        RouteEndpoint route => route.RoutePattern.RawText ?? Unknown,
        not null => Unknown,
        null => Unmatched
    };

    private static string Classify(int statusCode) => statusCode switch
    {
        >= 200 and <= 299 => "success",
        >= 400 and <= 499 => "client_error",
        >= 500 and <= 599 => "server_error",
        _ => "other"
    };
}
