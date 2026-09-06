using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ServiceMantle.Http;

namespace ServiceMantle.Management;

/// <summary>
/// Produces the two fixed public error results of the ServiceMantle management API v1 surface.
/// </summary>
/// <remarks>
/// Both results are closed: they accept no free text, no input value, no exception, and no custom
/// extension, and their <c>application/problem+json</c> body always contains exactly <c>type</c>,
/// <c>title</c>, <c>status</c>, <c>correlationId</c>, and <c>errorCode</c>. The Correlation ID is
/// the one already resolved for the request, so it matches the response header. They are meant for
/// a handler running inside the composed ServiceMantle pipeline before the response has started;
/// once a response has started, the sent response is left unchanged.
/// </remarks>
public static class ServiceMantleManagementApiResults
{
    /// <summary>
    /// Returns the fixed <c>400</c> / <c>management.request.invalid</c> result.
    /// </summary>
    /// <returns>The invalid-request result.</returns>
    public static IResult InvalidRequest() => ServiceMantleManagementApiProblemResult.InvalidRequest;

    /// <summary>
    /// Returns the fixed <c>409</c> / <c>management.request.conflict</c> result.
    /// </summary>
    /// <returns>The conflict result.</returns>
    public static IResult Conflict() => ServiceMantleManagementApiProblemResult.Conflict;
}

/// <summary>
/// The single serialization exit shared by the fixed management API v1 error results.
/// </summary>
internal sealed class ServiceMantleManagementApiProblemResult : IResult
{
    internal static readonly ServiceMantleManagementApiProblemResult InvalidRequest = new(
        StatusCodes.Status400BadRequest,
        ServiceMantleManagementApiDefaults.InvalidRequestErrorCode,
        ServiceMantleManagementApiDefaults.InvalidRequestTitle);

    internal static readonly ServiceMantleManagementApiProblemResult Conflict = new(
        StatusCodes.Status409Conflict,
        ServiceMantleManagementApiDefaults.ConflictErrorCode,
        ServiceMantleManagementApiDefaults.ConflictTitle);

    private readonly int statusCode;
    private readonly string errorCode;
    private readonly string title;
    private readonly string typeUri;

    private ServiceMantleManagementApiProblemResult(int statusCode, string errorCode, string title)
    {
        this.statusCode = statusCode;
        this.errorCode = errorCode;
        this.title = ServiceMantleProblemValue.ValidateTitle(title, nameof(title));
        typeUri = ServiceMantleProblemDetailsDefaults.CreateTypeUri(errorCode);
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = httpContext.Response;
        if (response.HasStarted)
        {
            // The sent response is the caller's result; a second, partially written body would be
            // worse than the response the handler already started.
            return;
        }

        var correlationId = CorrelationIdRequestSlot.Get(httpContext);
        if (correlationId is null)
        {
            correlationId = CorrelationIdValue.Generate();
            CorrelationIdRequestSlot.Set(httpContext, correlationId);
        }

        var body = Serialize(correlationId);
        response.StatusCode = statusCode;
        response.ContentType = "application/problem+json";
        response.ContentLength = body.Length;
        response.Headers[ServiceMantleHeaderNames.CorrelationId] = correlationId;
        await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
    }

    private byte[] Serialize(string correlationId)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("type", typeUri);
        writer.WriteString("title", title);
        writer.WriteNumber("status", statusCode);
        writer.WriteString(
            ServiceMantleProblemDetailsDefaults.CorrelationIdExtensionName,
            correlationId);
        writer.WriteString(ServiceMantleProblemDetailsDefaults.ErrorCodeExtensionName, errorCode);
        writer.WriteEndObject();
        writer.Flush();
        return buffer.WrittenSpan.ToArray();
    }
}
