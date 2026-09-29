using System.Buffers;
using System.Globalization;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace ServiceMantle.Web.Http;

internal sealed class ProblemDetailsMiddleware
{
    internal const string LoggerCategory = "ServiceMantle.Http.ProblemDetails";

    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.Web);

    private readonly RequestDelegate next;
    private readonly ILogger logger;

    public ProblemDetailsMiddleware(
        RequestDelegate next,
        ILoggerFactory loggerFactory)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(loggerFactory);

        this.next = next;
        logger = loggerFactory.CreateLogger(LoggerCategory);
    }

    public async Task InvokeAsync(
        HttpContext context,
        ExceptionMappingRegistry mappingRegistry)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(mappingRegistry);

        try
        {
            await next(context).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            context.RequestAborted.IsCancellationRequested && !context.Response.HasStarted)
        {
            throw;
        }
        catch (Exception exception)
        {
            if (context.Response.HasStarted)
            {
                TryLogError(
                    "A ServiceMantle request failed after the response started; the sent response was left unchanged.");
                return;
            }

            await WriteProblemAsync(context, exception, mappingRegistry).ConfigureAwait(false);
        }
    }

    private async Task WriteProblemAsync(
        HttpContext context,
        Exception exception,
        ExceptionMappingRegistry mappingRegistry)
    {
        var correlationId = CorrelationIdRequestSlot.Get(context);
        if (correlationId is null)
        {
            correlationId = CorrelationIdValue.Generate();
            CorrelationIdRequestSlot.Set(context, correlationId);
        }

        var mapping = mappingRegistry.TryGet(exception.GetType(), out var registered)
            ? registered
            : null;

        byte[] body;
        int statusCode;
        string errorCode;
        ExceptionMappingVariant? selection;
        try
        {
            selection = mapping?.Select(exception);
            statusCode = selection?.StatusCode ?? StatusCodes.Status500InternalServerError;
            errorCode = selection?.ErrorCode ??
                ProblemDetailsDefaults.InternalServerErrorCode;
            body = Serialize(
                selection?.TypeUri ?? ProblemDetailsDefaults.InternalServerErrorType,
                selection?.Title ?? ProblemDetailsDefaults.InternalServerErrorTitle,
                statusCode,
                correlationId,
                errorCode,
                exception,
                selection?.ExtensionFactories ?? []);
        }
        catch (Exception)
        {
            // A failing condition, extension factory, or serialization fails closed: the complete
            // response falls back to the generic 500 and headers from the failing selection are
            // not applied.
            selection = null;
            statusCode = StatusCodes.Status500InternalServerError;
            errorCode = ProblemDetailsDefaults.InternalServerErrorCode;
            body = Serialize(
                ProblemDetailsDefaults.InternalServerErrorType,
                ProblemDetailsDefaults.InternalServerErrorTitle,
                statusCode,
                correlationId,
                errorCode,
                exception,
                []);
        }

        var response = context.Response;
        response.StatusCode = statusCode;
        response.ContentType = "application/problem+json";
        response.ContentLength = body.Length;
        response.Headers[ServiceHeaderNames.CorrelationId] = correlationId;
        if (selection?.RetryAfterSeconds is int retryAfterSeconds)
        {
            response.Headers.RetryAfter =
                retryAfterSeconds.ToString(CultureInfo.InvariantCulture);
        }

        TryLogError(
            "A ServiceMantle request failed with {ErrorCode}; CorrelationId {CorrelationId}.",
            errorCode,
            correlationId);

        await response.Body.WriteAsync(body, context.RequestAborted).ConfigureAwait(false);
    }

    private static byte[] Serialize(
        string typeUri,
        string title,
        int statusCode,
        string correlationId,
        string errorCode,
        Exception exception,
        IReadOnlyList<ProblemExtensionFactory> extensionFactories)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", typeUri);
        writer.WriteString("title", title);
        writer.WriteNumber("status", statusCode);
        writer.WriteString(
            ProblemDetailsDefaults.CorrelationIdExtensionName,
            correlationId);
        writer.WriteString(ProblemDetailsDefaults.ErrorCodeExtensionName, errorCode);

        foreach (var extensionFactory in extensionFactories)
        {
            writer.WritePropertyName(extensionFactory.Name);
            var value = extensionFactory.GetValue(exception);
            JsonSerializer.Serialize(writer, value, SerializerOptions);
        }

        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }

    private void TryLogError(string message, params object?[] arguments)
    {
        try
        {
            logger.LogError(message, arguments);
        }
        catch (Exception)
        {
            // A logging provider must not turn a safe response into a second request failure.
        }
    }
}
