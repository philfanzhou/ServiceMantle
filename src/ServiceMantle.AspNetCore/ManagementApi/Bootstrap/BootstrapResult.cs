using System.Text;
using Microsoft.AspNetCore.Http;

namespace ServiceMantle.AspNetCore.ManagementApi.Bootstrap;

/// <summary>
/// The single serialization exit of the Bootstrap management entries.
/// </summary>
/// <remarks>
/// Every body is one of three fixed byte arrays built at type initialization. No connection string,
/// MasterKey, credential, provider id, server version, file path, validator code, or exception can
/// reach a serializer here: the success bodies project one boolean, and the failure bodies carry
/// only their closed error code.
/// </remarks>
internal sealed class BootstrapResult : IResult
{
    /// <summary>The created file, which only a restart activates.</summary>
    internal static readonly BootstrapResult Created = Json(
        StatusCodes.Status201Created);

    /// <summary>The replaced file, which only a restart activates.</summary>
    internal static readonly BootstrapResult Updated = Json(
        StatusCodes.Status200OK);

    /// <summary>The single rejection every unusable credential shares.</summary>
    internal static readonly BootstrapResult CredentialInvalid = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + BootstrapMapping.CredentialInvalidErrorCode + "\"}"),
        StatusCodes.Status401Unauthorized);

    /// <summary>The single rejection every store, validator, or internal failure shares.</summary>
    internal static readonly BootstrapResult Unavailable = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + BootstrapMapping.UnavailableErrorCode + "\"}"),
        StatusCodes.Status503ServiceUnavailable);

    private readonly byte[] body;
    private readonly int statusCode;

    private BootstrapResult(byte[] body, int statusCode)
    {
        this.body = body;
        this.statusCode = statusCode;
    }

    private static BootstrapResult Json(int statusCode) =>
        new(Encoding.UTF8.GetBytes("{\"restartRequired\":true}"), statusCode);

    public async Task ExecuteAsync(HttpContext httpContext)
    {
        ArgumentNullException.ThrowIfNull(httpContext);

        var response = httpContext.Response;
        if (response.HasStarted)
        {
            // The sent response is the caller's result. A second, partially written body would be
            // worse, and nothing here restores the file or the credential.
            return;
        }

        response.StatusCode = statusCode;
        response.ContentType = "application/json";
        response.ContentLength = body.Length;
        await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
    }
}
