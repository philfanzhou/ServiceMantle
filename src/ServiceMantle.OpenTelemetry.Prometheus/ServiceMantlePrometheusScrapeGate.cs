using Microsoft.AspNetCore.Http;

namespace ServiceMantle.OpenTelemetry.Prometheus;

internal sealed class ServiceMantlePrometheusScrapeGate(
    RequestDelegate next,
    ServiceMantlePrometheusEndpointState endpointState)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (endpointState.IsStopping || !endpointState.ScrapeSlots.Wait(0))
        {
            Reject(context);
            return;
        }

        var originalResponseBody = context.Response.Body;
        var requestAborted = context.RequestAborted;
        try
        {
            if (HttpMethods.IsHead(context.Request.Method))
            {
                context.Response.Body = Stream.Null;
            }

            using var stopping = CancellationTokenSource.CreateLinkedTokenSource(
                requestAborted,
                endpointState.ApplicationStopping);
            context.RequestAborted = stopping.Token;
            await next(context);

            if (!context.Response.HasStarted &&
                context.Response.StatusCode == StatusCodes.Status500InternalServerError)
            {
                Reject(context);
            }
        }
        catch (OperationCanceledException) when (
            requestAborted.IsCancellationRequested || endpointState.IsStopping)
        {
            throw;
        }
        catch (Exception) when (!context.Response.HasStarted)
        {
            Reject(context);
        }
        finally
        {
            context.Response.Body = originalResponseBody;
            endpointState.ScrapeSlots.Release();
        }
    }

    private static void Reject(HttpContext context)
    {
        context.Response.StatusCode = StatusCodes.Status503ServiceUnavailable;
        context.Response.ContentType = null;
        context.Response.ContentLength = 0;
    }
}
