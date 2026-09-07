using System.Runtime.CompilerServices;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Owns the endpoint-local mapping rules and closed error code of the installation status entry.
/// </summary>
internal static class ServiceMantleInstallationStatusMapping
{
    /// <summary>The only error code the entry answers when it cannot project one coherent observation.</summary>
    internal const string UnavailableErrorCode = "management.status.unavailable";

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
        new("The ServiceMantle installation status entry must be mapped at most once.");

    internal static InvalidOperationException MissingCapability() =>
        new("The ServiceMantle installation status entry requires AddServiceMantleManagementApiV1, " +
            "AddServiceMantleInstallationStatus, and the shared management entry capability.");
}
