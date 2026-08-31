using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using ServiceMantle.Http;
using ServiceMantle.Management;

namespace ServiceMantle.AspNetCore;

internal static class ServiceMantleRateLimitingPolicy
{
    internal const string LoggerCategory = "ServiceMantle.Http.RateLimiting";
    private const string SetupNamespace = "setup:";
    private const string ManagementClientNamespace = "management-client:";
    private const string ManagementOperatorNamespace = "management-operator:";
    private const string UnknownClient = "unknown-client";

    internal static RateLimitPartition<string> SetupPartition(HttpContext context)
    {
        var settings = context.RequestServices
            .GetRequiredService<ServiceMantleRateLimitingSnapshotProvider>()
            .GetRequiredSnapshot()
            .Setup;
        return RateLimitPartition.GetSlidingWindowLimiter(
            SetupNamespace + ClientKey(context.Connection.RemoteIpAddress),
            _ => settings.CreateLimiterOptions());
    }

    internal static RateLimitPartition<string> ManagementPartition(HttpContext context)
    {
        var settings = context.RequestServices
            .GetRequiredService<ServiceMantleRateLimitingSnapshotProvider>()
            .GetRequiredSnapshot()
            .Management;
        var resolution = context.RequestServices
            .GetRequiredService<IManagementCurrentOperatorResolver>()
            .Resolve(context.User);
        var key = resolution.Status == ManagementCurrentOperatorStatus.Resolved
            ? ManagementOperatorNamespace + HashOperator(resolution.Identity!)
            : ManagementClientNamespace + ClientKey(context.Connection.RemoteIpAddress);
        return RateLimitPartition.GetSlidingWindowLimiter(
            key,
            _ => settings.CreateLimiterOptions());
    }

    internal static async ValueTask OnRejectedAsync(
        OnRejectedContext rejected,
        CancellationToken cancellationToken)
    {
        TryLog(rejected.HttpContext);
        if (rejected.HttpContext.Response.HasStarted)
        {
            return;
        }

        var response = rejected.HttpContext.Response;
        var correlationId = CorrelationIdRequestSlot.Get(rejected.HttpContext) ??
            CorrelationIdValue.Generate();
        CorrelationIdRequestSlot.Set(rejected.HttpContext, correlationId);

        if (rejected.Lease.TryGetMetadata(MetadataName.RetryAfter, out var retryAfter))
        {
            var seconds = Math.Max(1L, (long)Math.Ceiling(retryAfter.TotalSeconds));
            response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        }

        var body = JsonSerializer.SerializeToUtf8Bytes(new
        {
            type = ServiceMantleProblemDetailsDefaults.CreateTypeUri(
                ServiceMantleRateLimitingDefaults.RejectedErrorCode),
            title = "Too many requests.",
            status = StatusCodes.Status429TooManyRequests,
            correlationId,
            errorCode = ServiceMantleRateLimitingDefaults.RejectedErrorCode,
        });
        response.StatusCode = StatusCodes.Status429TooManyRequests;
        response.ContentType = "application/problem+json";
        response.ContentLength = body.Length;
        response.Headers[ServiceMantleHeaderNames.CorrelationId] = correlationId;
        await response.Body.WriteAsync(body, cancellationToken).ConfigureAwait(false);
    }

    private static string ClientKey(IPAddress? address)
    {
        if (address is null)
        {
            return UnknownClient;
        }

        var normalized = address.IsIPv4MappedToIPv6 ? address.MapToIPv4() : address;
        return normalized.ToString();
    }

    private static string HashOperator(ManagementIdentity identity)
    {
        var material = Encoding.UTF8.GetBytes(identity.Source.Value + "\0" + identity.OperatorId);
        return Convert.ToHexStringLower(SHA256.HashData(material));
    }

    private static void TryLog(HttpContext context)
    {
        try
        {
            context.RequestServices
                .GetRequiredService<ILoggerFactory>()
                .CreateLogger(LoggerCategory)
                .LogWarning("A ServiceMantle request was rejected by a named rate-limit policy.");
        }
        catch
        {
            // Diagnostics must not replace a safe rejection or expose request data.
        }
    }
}
