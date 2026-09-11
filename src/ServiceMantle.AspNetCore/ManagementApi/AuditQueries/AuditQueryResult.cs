using System.Buffers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using ServiceMantle.Audit;

namespace ServiceMantle.AspNetCore.ManagementApi.AuditQueries;

/// <summary>
/// The closed serialization exit for management audit query responses.
/// </summary>
internal sealed class AuditQueryResult : IResult
{
    internal static readonly AuditQueryResult Unavailable = new(
        Encoding.UTF8.GetBytes(
            "{\"errorCode\":\"" + AuditQueryMapping.UnavailableErrorCode + "\"}"),
        StatusCodes.Status503ServiceUnavailable);

    private readonly byte[] body;
    private readonly int statusCode;

    private AuditQueryResult(byte[] body, int statusCode)
    {
        this.body = body;
        this.statusCode = statusCode;
    }

    internal static AuditQueryResult Success(ManagementAuditQueryResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("items");
            foreach (var item in result.Items)
            {
                WriteItem(writer, item);
            }

            writer.WriteEndArray();
            writer.WriteNumber("page", result.Page);
            writer.WriteNumber("pageSize", result.PageSize);
            writer.WriteNumber("totalCount", result.TotalCount);
            WriteNullableString(writer, "continuationCursor", result.ContinuationCursor);
            writer.WriteBoolean("hasNextPage", result.HasNextPage);
            writer.WriteEndObject();
        }

        return new AuditQueryResult(buffer.WrittenSpan.ToArray(), StatusCodes.Status200OK);
    }

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

    private static void WriteItem(Utf8JsonWriter writer, ManagementAuditRecord item)
    {
        writer.WriteStartObject();
        writer.WriteString("id", item.Id.ToString("D"));
        writer.WriteStartObject("operator");
        WriteNullableString(writer, "operatorId", item.Operator.OperatorId);
        WriteNullableString(writer, "displayName", item.Operator.DisplayName);
        writer.WriteString("source", item.Operator.Source.Value);
        writer.WriteEndObject();
        writer.WriteString("action", item.Action.Value);
        writer.WriteStartObject("target");
        writer.WriteString("type", item.Target.Type.Value);
        writer.WriteString("id", item.Target.Id);
        writer.WriteEndObject();
        writer.WriteString("outcome", ToWireValue(item.Outcome));
        writer.WriteString("occurredAtUtc", item.OccurredAtUtc.ToUniversalTime());
        WriteNullableString(writer, "clientIp", item.ClientIp);
        WriteNullableString(writer, "correlationId", item.CorrelationId);
        WriteNullableString(writer, "securityDescription", item.SecurityDescription);
        writer.WriteStartObject("metadata");
        foreach (var pair in item.Metadata.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            writer.WriteString(pair.Key, pair.Value);
        }

        writer.WriteEndObject();
        writer.WriteEndObject();
    }

    private static void WriteNullableString(Utf8JsonWriter writer, string name, string? value)
    {
        if (value is null)
        {
            writer.WriteNull(name);
        }
        else
        {
            writer.WriteString(name, value);
        }
    }

    private static string ToWireValue(ManagementAuditOutcome outcome) => outcome switch
    {
        ManagementAuditOutcome.Unknown => "unknown",
        ManagementAuditOutcome.Success => "success",
        ManagementAuditOutcome.Failure => "failure",
        ManagementAuditOutcome.Denied => "denied",
        _ => throw new InvalidOperationException("The management audit outcome is unknown.")
    };
}
