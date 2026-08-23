using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

internal readonly record struct ManagementAuditContinuationCursor(
    DateTimeOffset LastOccurredAtUtc,
    Guid LastId,
    int NextPage,
    string QueryFingerprint)
{
    private const int Version = 2;

    internal static ManagementAuditContinuationCursor Create(
        ManagementAuditQuery query,
        DateTimeOffset lastOccurredAtUtc,
        Guid lastId) =>
        new(lastOccurredAtUtc, lastId, checked(query.Page + 1), ComputeQueryFingerprint(query));

    internal static string Encode(ManagementAuditContinuationCursor cursor)
    {
        var payload = new Payload(
            Version,
            cursor.LastOccurredAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            cursor.LastId,
            cursor.NextPage,
            cursor.QueryFingerprint);
        var bytes = JsonSerializer.SerializeToUtf8Bytes(payload);
        return Convert.ToBase64String(bytes)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
    }

    internal static ManagementAuditContinuationCursor Decode(string value)
    {
        try
        {
            var padded = value.Replace('-', '+').Replace('_', '/');
            padded = padded.PadRight(padded.Length + ((4 - padded.Length % 4) % 4), '=');
            var payload = JsonSerializer.Deserialize<Payload>(Convert.FromBase64String(padded));
            if (payload is null
                || payload.Version != Version
                || !DateTime.TryParse(
                    payload.LastOccurredAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var lastOccurredAtUtc)
                || payload.LastId == Guid.Empty
                || payload.NextPage < 2
                || payload.QueryFingerprint is null
                || payload.QueryFingerprint.Length != 64)
            {
                throw new FormatException();
            }

            return new ManagementAuditContinuationCursor(
                new DateTimeOffset(DateTime.SpecifyKind(lastOccurredAtUtc, DateTimeKind.Utc)),
                payload.LastId,
                payload.NextPage,
                payload.QueryFingerprint);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or JsonException)
        {
            throw new ManagementAuditException(
                "audit.query_cursor_invalid",
                "The audit query continuation cursor is invalid.",
                exception);
        }
    }

    internal bool Matches(ManagementAuditQuery query) =>
        NextPage == query.Page
        && string.Equals(QueryFingerprint, ComputeQueryFingerprint(query), StringComparison.Ordinal);

    private static string ComputeQueryFingerprint(ManagementAuditQuery query)
    {
        var canonical = string.Join(
            '\n',
            query.Action?.Value ?? string.Empty,
            query.TargetType?.Value ?? string.Empty,
            query.TargetId ?? string.Empty,
            query.OperatorId ?? string.Empty,
            query.FromUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            query.ToUtc?.UtcDateTime.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty,
            query.PageSize.ToString(CultureInfo.InvariantCulture),
            ((int)query.SortOrder).ToString(CultureInfo.InvariantCulture));

        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(canonical)));
    }

    private sealed record Payload(
        int Version,
        string LastOccurredAtUtc,
        Guid LastId,
        int NextPage,
        string QueryFingerprint);
}
