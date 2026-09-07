using System.Runtime.CompilerServices;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Owns the endpoint-local mapping rules and closed error codes of the Setup entries.
/// </summary>
internal static class ServiceMantleSetupMapping
{
    /// <summary>The single code every rejected Setup Code shares, whatever the reason.</summary>
    internal const string CredentialInvalidErrorCode = "management.setup.credential_invalid";

    /// <summary>The single code every store, executor, or internal failure shares.</summary>
    internal const string UnavailableErrorCode = "management.setup.unavailable";

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
        new("The ServiceMantle Setup entries must be mapped at most once.");

    internal static InvalidOperationException MissingExecutor() =>
        new("The ServiceMantle Setup completion entry requires an explicit consumer transaction " +
            "executor that commits the shared unit of work before it reports success.");

    internal static InvalidOperationException MissingCapability() =>
        new("The ServiceMantle Setup entries require AddServiceMantleManagementApiV1, " +
            "AddServiceMantleManagementEntries, and the composed ServiceMantle pipeline.");
}
