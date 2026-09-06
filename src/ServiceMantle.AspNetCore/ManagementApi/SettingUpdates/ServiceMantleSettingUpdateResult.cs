using System.Globalization;
using System.Text;
using Microsoft.AspNetCore.Http;

namespace ServiceMantle.AspNetCore;

internal sealed class ServiceMantleSettingUpdateResult : IResult
{
    internal static readonly ServiceMantleSettingUpdateResult Unavailable = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + ServiceMantleSettingUpdateMapping.UnavailableErrorCode + "\"}"),
        StatusCodes.Status503ServiceUnavailable);

    private readonly byte[] body;
    private readonly int statusCode;

    private ServiceMantleSettingUpdateResult(byte[] body, int statusCode)
    {
        this.body = body;
        this.statusCode = statusCode;
    }

    internal static ServiceMantleSettingUpdateResult Applied(long version) =>
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
