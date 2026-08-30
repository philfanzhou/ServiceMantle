using Microsoft.AspNetCore.Http;

namespace ServiceMantle.Http;

/// <summary>
/// Holds the resolved Correlation ID for one request under a private, non-collidable key.
/// </summary>
internal static class CorrelationIdRequestSlot
{
    private static readonly object Key = new();

    internal static void Set(HttpContext context, string correlationId)
    {
        context.Items[Key] = correlationId;
    }

    internal static string? Get(HttpContext context) =>
        context.Items.TryGetValue(Key, out var value) ? value as string : null;
}
