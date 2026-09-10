using System.Buffers;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ServiceMantle.AspNetCore.Logging;

namespace ServiceMantle.AspNetCore.ManagementApi.RuntimeInfo;

/// <summary>
/// The closed result of the management API v1 runtime information endpoint.
/// </summary>
/// <remarks>
/// The body is a fixed four-field projection built once from the immutable
/// <see cref="ServiceLogContext"/>. No request, configuration, environment, exception, or logging
/// object takes part in it, and the endpoint offers no extension point that could add a field.
/// </remarks>
internal sealed class RuntimeInfoResult : IResult
{
    /// <summary>The only phase value the endpoint can answer with.</summary>
    /// <remarks>
    /// The phase gate admits a normal management request only in <c>Completed</c> + <c>Succeeded</c>
    /// + <c>Reachable</c>, so the admitted request already observed this phase. A state change after
    /// the admission does not retract the request and is not re-read here.
    /// </remarks>
    internal const string AdmittedPhase = "Completed";

    private readonly byte[] body;

    private RuntimeInfoResult(byte[] body)
    {
        this.body = body;
    }

    internal static RuntimeInfoResult Create(ServiceLogContext logContext)
    {
        ArgumentNullException.ThrowIfNull(logContext);

        var buffer = new ArrayBufferWriter<byte>();
        using var writer = new Utf8JsonWriter(buffer);
        writer.WriteStartObject();
        writer.WriteString("serviceName", logContext.ServiceName);
        writer.WriteString("serviceVersion", logContext.ServiceVersion);
        writer.WriteString("instanceId", logContext.InstanceId);
        writer.WriteString("phase", AdmittedPhase);
        writer.WriteEndObject();
        writer.Flush();
        return new RuntimeInfoResult(buffer.WrittenSpan.ToArray());
    }

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = httpContext.Response;
        if (response.HasStarted)
        {
            // The sent response is the caller's result; a second, partially written body would be
            // worse than the response that already started.
            return;
        }

        response.StatusCode = StatusCodes.Status200OK;
        response.ContentType = "application/json";
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
