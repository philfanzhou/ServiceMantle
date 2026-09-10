using Microsoft.AspNetCore.Routing;
using ServiceMantle.AspNetCore.Logging;

namespace ServiceMantle.ReferenceService.Logging;

/// <summary>
/// Writes one fixed, sanitized log line per request. It never records the raw path, query, body,
/// connection settings, or exception detail, it collapses the request method to the framework's
/// known token set, and it never swallows cancellation. Header values are only what the DI-owned
/// <see cref="RequestHeaderDiagnosticProjector"/> emits: denied Header values become the
/// redaction marker, while the values of Headers outside the denied list are projected under the
/// free-text contract and therefore do reach the log line.
/// </summary>
internal sealed class ReferenceRequestLoggingMiddleware
{
    private const string Cancelled = "cancelled";
    private const string Faulted = "faulted";
    private const string OtherMethod = "(other)";
    private const string Unmatched = "(unmatched)";
    private const string Unknown = "(unknown)";

    private readonly RequestDelegate next;
    private readonly RequestHeaderDiagnosticProjector projector;
    private readonly ILogger logger;

    public ReferenceRequestLoggingMiddleware(
        RequestDelegate next,
        RequestHeaderDiagnosticProjector projector,
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
        var method = Method(context.Request.Method);
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

    /// <summary>
    /// Collapses the caller-supplied method token to a bounded set. A method the framework does not
    /// recognize is reported as a fixed placeholder, so the log line never carries caller text here.
    /// </summary>
    private static string Method(string method) =>
        HttpMethods.IsGet(method) ? HttpMethods.Get
        : HttpMethods.IsPost(method) ? HttpMethods.Post
        : HttpMethods.IsPut(method) ? HttpMethods.Put
        : HttpMethods.IsDelete(method) ? HttpMethods.Delete
        : HttpMethods.IsPatch(method) ? HttpMethods.Patch
        : HttpMethods.IsHead(method) ? HttpMethods.Head
        : HttpMethods.IsOptions(method) ? HttpMethods.Options
        : HttpMethods.IsTrace(method) ? HttpMethods.Trace
        : HttpMethods.IsConnect(method) ? HttpMethods.Connect
        : OtherMethod;

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
