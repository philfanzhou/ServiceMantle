using System.Globalization;
using System.Text.Json;
using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

internal readonly record struct ManagementAuditContinuationCursor(
    DateTimeOffset SnapshotOccurredAtUtc,
    Guid SnapshotId,
    DateTimeOffset LastOccurredAtUtc,
    Guid LastId,
    ManagementAuditSortOrder SortOrder)
{
    private const int Version = 1;

    internal static string Encode(ManagementAuditContinuationCursor cursor)
    {
        var payload = new Payload(
            Version,
            cursor.SortOrder.ToString(),
            cursor.SnapshotOccurredAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            cursor.SnapshotId,
            cursor.LastOccurredAtUtc.UtcDateTime.ToString("O", CultureInfo.InvariantCulture),
            cursor.LastId);
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
                || !Enum.TryParse<ManagementAuditSortOrder>(payload.SortOrder, out var sortOrder)
                || !Enum.IsDefined(sortOrder)
                || !DateTime.TryParse(
                    payload.SnapshotOccurredAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var snapshotOccurredAtUtc)
                || !DateTime.TryParse(
                    payload.LastOccurredAtUtc,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind,
                    out var lastOccurredAtUtc)
                || payload.SnapshotId == Guid.Empty
                || payload.LastId == Guid.Empty)
            {
                throw new FormatException();
            }

            return new ManagementAuditContinuationCursor(
                new DateTimeOffset(DateTime.SpecifyKind(snapshotOccurredAtUtc, DateTimeKind.Utc)),
                payload.SnapshotId,
                new DateTimeOffset(DateTime.SpecifyKind(lastOccurredAtUtc, DateTimeKind.Utc)),
                payload.LastId,
                sortOrder);
        }
        catch (Exception exception) when (exception is FormatException or InvalidOperationException or JsonException)
        {
            throw new ManagementAuditException(
                "audit.query_cursor_invalid",
                "The audit query continuation cursor is invalid.",
                exception);
        }
    }

    private sealed record Payload(
        int Version,
        string SortOrder,
        string SnapshotOccurredAtUtc,
        Guid SnapshotId,
        string LastOccurredAtUtc,
        Guid LastId);
}
