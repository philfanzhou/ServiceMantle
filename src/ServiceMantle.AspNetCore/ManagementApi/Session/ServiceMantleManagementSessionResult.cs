using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// The single serialization exit of the management session entries.
/// </summary>
/// <remarks>
/// The success projection is written field by field from an already validated identity. No claims
/// principal, authentication ticket, authentication properties, provider result, provider error
/// code, or exception ever reaches a serializer here, so the operator identifier, the display name,
/// the identity source, and any upstream detail cannot appear in a response. <c>HEAD</c> answers the
/// same status code and headers as <c>GET</c> with no body.
/// </remarks>
internal sealed class ServiceMantleManagementSessionResult : IResult
{
    /// <summary>
    /// The fixed 401 of a login that produced no acceptable credentials. It reuses the existing
    /// session error code and body shape, and is written directly rather than through a challenge so
    /// that an anonymous login response cannot reveal whether a cookie was presented.
    /// </summary>
    internal static readonly ServiceMantleManagementSessionResult Unauthenticated = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" +
            ServiceMantleManagementSessionDefaults.UnauthenticatedErrorCode + "\"}"),
        StatusCodes.Status401Unauthorized);

    /// <summary>
    /// The single rejection every provider failure, adapter failure, invalid result, internal
    /// cancellation, internal timeout, and sign-in or sign-out failure shares.
    /// </summary>
    internal static readonly ServiceMantleManagementSessionResult Unavailable = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + ServiceMantleManagementSessionMapping.UnavailableErrorCode + "\"}"),
        StatusCodes.Status503ServiceUnavailable);

    /// <summary>The empty success of a completed sign-in or sign-out.</summary>
    internal static readonly ServiceMantleManagementSessionResult NoContent = new(
        [],
        StatusCodes.Status204NoContent);

    private readonly byte[] body;
    private readonly int statusCode;

    private ServiceMantleManagementSessionResult(byte[] body, int statusCode)
    {
        this.body = body;
        this.statusCode = statusCode;
    }

    /// <summary>
    /// Builds the current-session projection: the fixed authenticated flag, the UTC ticket expiry,
    /// and the defined permission names in the fixed <see cref="ManagementPermission"/> order.
    /// </summary>
    internal static ServiceMantleManagementSessionResult Current(
        ManagementIdentity identity,
        DateTimeOffset expiresAtUtc)
    {
        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteBoolean("authenticated", true);
            writer.WriteString("expiresAtUtc", expiresAtUtc.UtcDateTime);
            writer.WriteStartArray("permissions");
            foreach (var permission in identity.Permissions)
            {
                writer.WriteStringValue(ManagementPermissions.ToWireValue(permission));
            }

            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        return new ServiceMantleManagementSessionResult(
            buffer.WrittenSpan.ToArray(),
            StatusCodes.Status200OK);
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

        response.StatusCode = statusCode;
        if (body.Length == 0)
        {
            return;
        }

        response.ContentType = statusCode == StatusCodes.Status200OK
            ? "application/json"
            // The two rejections keep the exact content type of the existing session responses.
            : "application/json; charset=utf-8";
        response.ContentLength = body.Length;
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            // HEAD answers the same status code and headers as GET, and no body at all.
            return;
        }

        await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
