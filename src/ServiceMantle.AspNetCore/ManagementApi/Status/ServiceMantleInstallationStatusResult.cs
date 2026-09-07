using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ServiceMantle.Health;
using ServiceMantle.Installation;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// The single serialization exit of the anonymous installation status entry.
/// </summary>
/// <remarks>
/// The success body is a fixed five-field projection written field by field. No snapshot,
/// <c>BootstrapManagementStatus</c>, configuration, path, or exception object ever reaches a
/// serializer, so ServiceId, InstanceId, provider, server version, connection string, MasterKey, and
/// source error codes cannot appear here. <c>HEAD</c> produces the same status code and headers as
/// <c>GET</c> and writes no body.
/// </remarks>
internal sealed class ServiceMantleInstallationStatusResult : IResult
{
    /// <summary>
    /// The only rejection the entry answers. It carries the closed error code and nothing else:
    /// no phase, no Bootstrap detail, no internal classification, and no exception text.
    /// </summary>
    internal static readonly ServiceMantleInstallationStatusResult Unavailable = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + ServiceMantleInstallationStatusMapping.UnavailableErrorCode + "\"}"),
        StatusCodes.Status503ServiceUnavailable);

    private readonly byte[] body;
    private readonly int statusCode;

    private ServiceMantleInstallationStatusResult(byte[] body, int statusCode)
    {
        this.body = body;
        this.statusCode = statusCode;
    }

    /// <summary>
    /// Builds the success projection, or the fixed rejection when a sampled value has no fixed wire
    /// name. A value outside the defined enumerations is treated as unobservable, never guessed.
    /// </summary>
    internal static ServiceMantleInstallationStatusResult Create(
        ServiceHealthSnapshot snapshot,
        bool bootstrapConfigured,
        bool restartRequired)
    {
        if (ToWireValue(snapshot.Phase) is not { } phase ||
            ToWireValue(snapshot.MigrationStatus) is not { } migrationStatus ||
            ToWireValue(snapshot.DatabaseStatus) is not { } databaseStatus)
        {
            return Unavailable;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteString("phase", phase);
            writer.WriteString("migrationStatus", migrationStatus);
            writer.WriteString("databaseStatus", databaseStatus);
            writer.WriteBoolean("bootstrapConfigured", bootstrapConfigured);
            writer.WriteBoolean("restartRequired", restartRequired);
            writer.WriteEndObject();
        }

        return new ServiceMantleInstallationStatusResult(
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
        response.ContentType = "application/json";
        response.ContentLength = body.Length;
        if (HttpMethods.IsHead(httpContext.Request.Method))
        {
            // HEAD answers the same status code and headers as GET, and no body at all.
            return;
        }

        await response.Body.WriteAsync(body, httpContext.RequestAborted).ConfigureAwait(false);
    }

    private static string? ToWireValue(ServiceStartupPhase phase) => phase switch
    {
        ServiceStartupPhase.BootstrapConfiguration => "bootstrap_configuration",
        ServiceStartupPhase.PendingSetup => "pending_setup",
        ServiceStartupPhase.Completed => "completed",
        _ => null,
    };

    private static string? ToWireValue(ServiceMigrationReadinessState migrationStatus) => migrationStatus switch
    {
        ServiceMigrationReadinessState.NotStarted => "not_started",
        ServiceMigrationReadinessState.Running => "running",
        ServiceMigrationReadinessState.Succeeded => "succeeded",
        ServiceMigrationReadinessState.Failed => "failed",
        _ => null,
    };

    private static string? ToWireValue(ServiceDatabaseReadinessState databaseStatus) => databaseStatus switch
    {
        ServiceDatabaseReadinessState.Reachable => "reachable",
        ServiceDatabaseReadinessState.Unreachable => "unreachable",
        _ => null,
    };
}
