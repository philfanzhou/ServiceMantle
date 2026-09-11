using System.Runtime.CompilerServices;

namespace ServiceMantle.AspNetCore.ManagementApi.Session;

/// <summary>
/// Owns the endpoint-local mapping rules, limits, and closed error code of the session entries.
/// </summary>
internal static class ManagementSessionMapping
{
    /// <summary>The single code every provider, adapter, sign-in, or internal failure shares.</summary>
    internal const string UnavailableErrorCode = "management.session.unavailable";

    /// <summary>The raw login request body ServiceMantle admits, counted in bytes.</summary>
    internal const long MaximumLoginBodyLength = 64 * 1024;

    /// <summary>
    /// Counts the mappings per host. The application's service provider is already built when an
    /// endpoint is mapped, so this per-host state cannot live in the container.
    /// </summary>
    private static readonly ConditionalWeakTable<IServiceProvider, StrongBox<int>> Mappings = new();

    internal static void RecordMap(IServiceProvider services)
    {
        var count = Mappings.GetValue(services, static _ => new StrongBox<int>(0));
        if (Interlocked.Increment(ref count.Value) != 1)
        {
            throw Failure();
        }
    }

    internal static InvalidOperationException Failure() =>
        new("The ServiceMantle management session entries must be mapped at most once.");

    internal static InvalidOperationException MissingAdapter() =>
        new("The ServiceMantle management login entry requires an explicit consumer login adapter " +
            "that obtains credentials through its own trusted scoped accessor.");

    internal static InvalidOperationException InvalidLoginTimeout() =>
        new("The ServiceMantle management login timeout must be between 100 milliseconds and 30 seconds.");
}
