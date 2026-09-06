using System.Runtime.CompilerServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Owns the mapping rules and the bounded input parsing of the management API v1 setting queries.
/// </summary>
internal static class ServiceMantleSettingQueryMapping
{
    /// <summary>The definition-catalog route below the versioned management root.</summary>
    internal const string DefinitionsPath = "/settings/definitions";

    /// <summary>The current-value route below the versioned management root.</summary>
    internal const string CurrentValuesPath = "/settings";

    /// <summary>The only accepted query parameter name, matched exactly.</summary>
    internal const string GroupParameterName = "group";

    /// <summary>The fixed error code answered when a complete refresh is unavailable.</summary>
    internal const string UnavailableErrorCode = "management.settings.unavailable";

    private const int MaximumGroupLength = 128;

    /// <summary>
    /// Counts the mappings per host. The application's service provider is already built when an
    /// endpoint is mapped, so this per-host state cannot live in the container, and the endpoints are
    /// explicitly not allowed to extend the shared management API v1 registration.
    /// </summary>
    private static readonly ConditionalWeakTable<IServiceProvider, StrongBox<int>> Mappings = new();

    internal static bool HasQueryService(IServiceProvider services)
    {
        try
        {
            return services.GetService<ServiceSettingQueryService>() is not null;
        }
        catch
        {
            // A catalog, store or root-key registration that cannot be constructed is a missing
            // capability, and its own diagnostics are not repeated here.
            return false;
        }
    }

    internal static void RecordMap(IServiceProvider services)
    {
        var count = Mappings.GetValue(services, static _ => new StrongBox<int>(0));
        if (Interlocked.Increment(ref count.Value) != 1)
        {
            throw Failure();
        }
    }

    /// <summary>
    /// Rejects an endpoint that did not end up as a direct child of the protected management API v1
    /// group. The check runs as a final convention, so it sees the group's own metadata and the
    /// combined route pattern, and it fails while the endpoints are built rather than on a request.
    /// </summary>
    internal static void Validate(EndpointBuilder builder, string expectedPath)
    {
        if (builder is not RouteEndpointBuilder route ||
            !string.Equals(route.RoutePattern.RawText, expectedPath, StringComparison.Ordinal) ||
            builder.Metadata.OfType<ServiceMantleManagementApiMetadata>().Count() != 1)
        {
            throw Failure();
        }
    }

    /// <summary>
    /// Accepts at most one <c>group</c> query value and rejects every other input shape.
    /// </summary>
    /// <remarks>
    /// The group is a filter over already normalized setting keys, so it is normalized the same way
    /// and is bounded before it is trimmed. There is no group domain model: an accepted value that
    /// matches nothing is an empty result, not an error.
    /// </remarks>
    internal static bool TryParseGroup(HttpContext context, out string? group)
    {
        group = null;
        var query = context.Request.Query;
        foreach (var parameter in query)
        {
            if (!string.Equals(parameter.Key, GroupParameterName, StringComparison.Ordinal))
            {
                return false;
            }
        }

        if (!query.TryGetValue(GroupParameterName, out var values))
        {
            return true;
        }

        if (values.Count != 1)
        {
            return false;
        }

        var raw = values[0];
        if (raw is null || raw.Length > MaximumGroupLength)
        {
            return false;
        }

        var normalized = raw.Trim().ToLowerInvariant();
        if (!IsNormalizedGroup(normalized))
        {
            return false;
        }

        group = normalized;
        return true;
    }

    internal static InvalidOperationException Failure() =>
        new("The ServiceMantle management API v1 setting query endpoints must be mapped at most " +
            "once, and only on the route group returned by MapServiceMantleManagementApiV1.");

    internal static InvalidOperationException MissingQueryService() =>
        new("The ServiceMantle management API v1 setting query endpoints require the setting " +
            "snapshot capability added by AddServiceMantleSettingSnapshots.");

    private static bool IsNormalizedGroup(string group)
    {
        if (group.Length == 0 || !IsLowerAsciiLetterOrDigit(group[0]))
        {
            return false;
        }

        for (var index = 1; index < group.Length; index++)
        {
            var character = group[index];
            if (!IsLowerAsciiLetterOrDigit(character) && character is not ('.' or '_' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsLowerAsciiLetterOrDigit(char character) =>
        character is >= 'a' and <= 'z' or >= '0' and <= '9';
}
