using System.Text;
using Microsoft.AspNetCore.Http;

namespace ServiceMantle.AspNetCore.ManagementApi.SettingQueries;

/// <summary>
/// The single serialization exit shared by the management API v1 setting query responses.
/// </summary>
internal sealed class SettingQueryResult : IResult
{
    /// <summary>
    /// The fixed rejection answered when one complete snapshot could not be projected.
    /// </summary>
    /// <remarks>
    /// The body carries the closed error code only. It never contains a version, a key, an internal
    /// error classification, or a partial or previously activated value.
    /// </remarks>
    internal static readonly SettingQueryResult Unavailable = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + SettingQueryMapping.UnavailableErrorCode + "\"}"),
        StatusCodes.Status503ServiceUnavailable);

    private readonly byte[] body;
    private readonly int statusCode;

    private SettingQueryResult(byte[] body, int statusCode)
    {
        this.body = body;
        this.statusCode = statusCode;
    }

    internal static SettingQueryResult Json(byte[] body) =>
        new(body, StatusCodes.Status200OK);

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
        response.ContentType = "application/json";
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
