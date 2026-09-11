using System.Text;
using Microsoft.AspNetCore.Http;

namespace ServiceMantle.AspNetCore.ManagementApi.Setup;

/// <summary>
/// The single serialization exit of the Setup entries.
/// </summary>
/// <remarks>
/// Every body is one of four fixed byte arrays built at type initialization. No candidate Setup
/// Code, digest, generation, expiry, installation version, contributor value, store error code, or
/// exception can reach a serializer here, and the two failure bodies carry only their closed error
/// code. <c>HEAD</c> answers the same status code and headers as <c>GET</c> with no body.
/// </remarks>
internal sealed class SetupResult : IResult
{
    /// <summary>The only two status projections the read entry can answer with.</summary>
    internal static readonly SetupResult Pending = Json("{\"status\":\"pending\"}");

    internal static readonly SetupResult Completed = Json("{\"status\":\"completed\"}");

    /// <summary>The single rejection every invalid, expired, replayed, or racing code shares.</summary>
    internal static readonly SetupResult CredentialInvalid = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + SetupMapping.CredentialInvalidErrorCode + "\"}"),
        StatusCodes.Status401Unauthorized);

    /// <summary>The single rejection every store, executor, or internal failure shares.</summary>
    internal static readonly SetupResult Unavailable = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + SetupMapping.UnavailableErrorCode + "\"}"),
        StatusCodes.Status503ServiceUnavailable);

    /// <summary>The empty success of a committed completion.</summary>
    internal static readonly SetupResult NoContent = new(
        [],
        StatusCodes.Status204NoContent);

    private readonly byte[] body;
    private readonly int statusCode;

    private SetupResult(byte[] body, int statusCode)
    {
        this.body = body;
        this.statusCode = statusCode;
    }

    private static SetupResult Json(string body) =>
        new(Encoding.UTF8.GetBytes(body), StatusCodes.Status200OK);

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

        response.ContentType = "application/json";
        response.ContentLength = body.Length;
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            // HEAD answers the same status code and headers as GET, and no body at all.
            return;
        }

        await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
