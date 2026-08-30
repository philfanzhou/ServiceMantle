using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using Microsoft.Net.Http.Headers;

namespace ServiceMantle.Management;

internal static class ServiceMantleManagementCookieEvents
{
    internal static CookieAuthenticationEvents Create() => new()
    {
        OnRedirectToLogin = context => WriteAsync(
            context.Response,
            context.Request.Cookies.ContainsKey(ServiceMantleManagementSessionDefaults.CookieName)
                ? ServiceMantleManagementSessionDefaults.ExpiredErrorCode
                : ServiceMantleManagementSessionDefaults.UnauthenticatedErrorCode,
            StatusCodes.Status401Unauthorized,
            context.HttpContext.RequestAborted),
        OnRedirectToAccessDenied = context => WriteAsync(
            context.Response,
            ServiceMantleManagementSessionDefaults.ForbiddenErrorCode,
            StatusCodes.Status403Forbidden,
            context.HttpContext.RequestAborted),
    };

    private static Task WriteAsync(
        HttpResponse response,
        string errorCode,
        int statusCode,
        CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.Headers.Remove(HeaderNames.Location);
        response.Headers.Remove(HeaderNames.WWWAuthenticate);
        response.Headers.Remove(HeaderNames.SetCookie);
        response.ContentType = "application/json; charset=utf-8";
        return response.WriteAsync(
            JsonSerializer.Serialize(new SessionError(errorCode)),
            cancellationToken);
    }

    private sealed record SessionError(
        [property: JsonPropertyName("errorCode")] string ErrorCode);
}
