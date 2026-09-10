using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Audit;

namespace ServiceMantle.AspNetCore.ManagementApi.AuditQueries;

/// <summary>
/// Owns mapping validation and bounded HTTP input parsing for management audit queries.
/// </summary>
internal static class AuditQueryMapping
{
    internal const string Path = "/audit";
    internal const string UnavailableErrorCode = "management.audit.unavailable";

    private const int MaximumTimeLength = 40;

    private static readonly HashSet<string> ParameterNames = new(StringComparer.Ordinal)
    {
        "action",
        "targetType",
        "targetId",
        "operatorId",
        "fromUtc",
        "toUtc",
        "page",
        "pageSize",
        "sortOrder",
        "cursor"
    };

    private static readonly ConditionalWeakTable<IServiceProvider, StrongBox<int>> Mappings = new();

    internal static bool HasQueryService(IServiceProvider services) =>
        services.GetService<IServiceProviderIsService>()?
            .IsService(typeof(IManagementAuditQueryService)) == true;

    internal static void RecordMap(IServiceProvider services)
    {
        var count = Mappings.GetValue(services, static _ => new StrongBox<int>(0));
        if (Interlocked.Increment(ref count.Value) != 1)
        {
            throw Failure();
        }
    }

    internal static void Validate(EndpointBuilder builder, string expectedPath)
    {
        if (builder is not RouteEndpointBuilder route
            || !string.Equals(route.RoutePattern.RawText, expectedPath, StringComparison.Ordinal)
            || builder.Metadata.OfType<ManagementApiMetadata>().Count() != 1)
        {
            throw Failure();
        }
    }

    internal static bool TryParse(HttpContext context, out ManagementAuditQuery? query)
    {
        query = null;
        if (context.Request.ContentLength is > 0
            || context.Request.Headers.ContainsKey("Transfer-Encoding"))
        {
            return false;
        }

        var values = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (var parameter in context.Request.Query)
        {
            if (!ParameterNames.Contains(parameter.Key)
                || parameter.Value.Count != 1
                || string.IsNullOrEmpty(parameter.Value[0]))
            {
                return false;
            }

            values.Add(parameter.Key, parameter.Value[0]);
        }

        if (!TryBound(values, "action", ManagementAuditAction.MaxLength, out var rawAction)
            || !TryBound(values, "targetType", ManagementAuditTargetType.MaxLength, out var rawTargetType)
            || !TryBound(values, "targetId", ManagementAuditTarget.MaxTargetIdLength, out var targetId)
            || !TryBound(values, "operatorId", ManagementAuditOperator.MaxOperatorIdLength, out var operatorId)
            || !TryBound(values, "cursor", ManagementAuditQuery.MaxCursorLength, out var cursor)
            || !TryPositiveInt(values, "page", 1, int.MaxValue, out var page)
            || !TryPositiveInt(values, "pageSize", ManagementAuditQuery.DefaultPageSize,
                ManagementAuditQuery.MaxPageSize, out var pageSize)
            || !TrySortOrder(values, out var sortOrder)
            || !TryTime(values, "fromUtc", out var fromUtc)
            || !TryTime(values, "toUtc", out var toUtc)
            || page == 1 && cursor is not null
            || page > 1 && cursor is null)
        {
            return false;
        }

        try
        {
            var action = rawAction is null ? null : ManagementAuditAction.Parse(rawAction);
            var targetType = rawTargetType is null ? null : ManagementAuditTargetType.Parse(rawTargetType);
            query = ManagementAuditQuery.Create(
                action,
                targetType,
                targetId,
                operatorId,
                fromUtc,
                toUtc,
                page,
                pageSize,
                sortOrder,
                cursor);
            if (targetId is not null && query.TargetId is null
                || operatorId is not null && query.OperatorId is null
                || cursor is not null && query.Cursor is null)
            {
                query = null;
                return false;
            }

            return true;
        }
        catch (ManagementAuditException)
        {
            return false;
        }
    }

    internal static InvalidOperationException Failure() =>
        new("The ServiceMantle management API v1 audit query endpoint must be mapped at most once, " +
            "and only on the route group returned by MapServiceMantleManagementApiV1.");

    internal static InvalidOperationException MissingQueryService() =>
        new("The ServiceMantle management API v1 audit query endpoint requires a registered " +
            "IManagementAuditQueryService.");

    private static bool TryBound(
        IReadOnlyDictionary<string, string?> values,
        string name,
        int maximumLength,
        out string? value)
    {
        values.TryGetValue(name, out value);
        return value is null || value.Length <= maximumLength;
    }

    private static bool TryPositiveInt(
        IReadOnlyDictionary<string, string?> values,
        string name,
        int defaultValue,
        int maximum,
        out int value)
    {
        if (!values.TryGetValue(name, out var raw))
        {
            value = defaultValue;
            return true;
        }

        value = 0;
        return raw is not null
            && raw.AsSpan().ContainsOnlyAsciiDigits()
            && int.TryParse(raw, NumberStyles.None, CultureInfo.InvariantCulture, out value)
            && value >= 1
            && value <= maximum;
    }

    private static bool TrySortOrder(
        IReadOnlyDictionary<string, string?> values,
        out ManagementAuditSortOrder sortOrder)
    {
        if (!values.TryGetValue("sortOrder", out var raw))
        {
            sortOrder = ManagementAuditSortOrder.Newest;
            return true;
        }

        if (string.Equals(raw, "newest", StringComparison.Ordinal))
        {
            sortOrder = ManagementAuditSortOrder.Newest;
            return true;
        }

        if (string.Equals(raw, "oldest", StringComparison.Ordinal))
        {
            sortOrder = ManagementAuditSortOrder.Oldest;
            return true;
        }

        sortOrder = default;
        return false;
    }

    private static bool TryTime(
        IReadOnlyDictionary<string, string?> values,
        string name,
        out DateTimeOffset? value)
    {
        value = null;
        if (!values.TryGetValue(name, out var raw))
        {
            return true;
        }

        if (raw is null || raw.Length > MaximumTimeLength || raw.Length < 20)
        {
            return false;
        }

        var timeZoneStart = raw.EndsWith('Z') ? raw.Length - 1 : raw.Length - 6;
        var hasZulu = timeZoneStart >= 19 && raw[timeZoneStart] == 'Z';
        var hasOffset = timeZoneStart >= 19
            && (raw[timeZoneStart] is '+' or '-')
            && raw.Length - timeZoneStart == 6
            && raw[timeZoneStart + 3] == ':';
        if (!hasZulu && !hasOffset)
        {
            return false;
        }

        var fractionLength = timeZoneStart - 19;
        if (fractionLength != 0
            && (fractionLength is < 2 or > 8
                || raw[19] != '.'
                || !raw.AsSpan(20, fractionLength - 1).ContainsOnlyAsciiDigits()))
        {
            return false;
        }

        var format = "yyyy-MM-dd'T'HH:mm:ss"
            + (fractionLength == 0 ? "" : "." + new string('f', fractionLength - 1))
            + (hasZulu ? "'Z'" : "zzz");
        if (!DateTimeOffset.TryParseExact(
            raw,
            format,
            CultureInfo.InvariantCulture,
            hasZulu ? DateTimeStyles.AssumeUniversal : DateTimeStyles.None,
            out var parsed))
        {
            return false;
        }

        value = parsed.ToUniversalTime();
        return true;
    }

    private static bool ContainsOnlyAsciiDigits(this ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}
