using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;

internal sealed class SettingUpdateResult : IResult
{
    internal static readonly SettingUpdateResult Unavailable = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + SettingUpdateMapping.UnavailableErrorCode + "\"}"),
        StatusCodes.Status503ServiceUnavailable);

    private readonly byte[] body;
    private readonly int statusCode;

    private SettingUpdateResult(byte[] body, int statusCode)
    {
        this.body = body;
        this.statusCode = statusCode;
    }

    internal static SettingUpdateResult Applied(long version) =>
        new(
            Encoding.UTF8.GetBytes("{\"version\":" + version.ToString(CultureInfo.InvariantCulture) + "}"),
            StatusCodes.Status200OK);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);
        if (httpContext.Response.HasStarted)
        {
            return;
        }

        httpContext.Response.StatusCode = statusCode;
        httpContext.Response.ContentType = "application/json";
        httpContext.Response.ContentLength = body.Length;
        await httpContext.Response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
