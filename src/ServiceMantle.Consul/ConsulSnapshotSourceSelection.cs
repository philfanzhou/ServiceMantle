using Microsoft.Extensions.DependencyInjection;
using ServiceMantle.Configuration;

namespace ServiceMantle.Consul;

/// <summary>
/// Pins which setting-snapshot source the Consul client boundary reads: the process-global
/// <see cref="IServiceSettingCurrentSnapshotAccessor"/> (a null typed type), or exactly one
/// dedicated accessor type supplied through the typed registration entry.
/// </summary>
/// <remarks>
/// Repeated registrations must agree on the source, exactly like the timing and advertisement
/// copies: the same source repeatedly is idempotent, while a default/typed mix or two different
/// accessor types is a conflicting configuration rejected with
/// <see cref="ConsulConfigurationError.InvalidConfiguration"/> when the lifecycle is first
/// resolved - before any background work starts.
/// </remarks>
internal sealed record ConsulSnapshotSourceSelection(Type? TypedAccessor)
{
    internal static readonly ConsulSnapshotSourceSelection Global = new(TypedAccessor: null);

    /// <summary>Accepts repeated source registrations only while they agree on the source.</summary>
    internal static ConsulSnapshotSourceSelection Single(IEnumerable<ConsulSnapshotSourceSelection> registered)
    {
        ConsulSnapshotSourceSelection? baseline = null;
        foreach (var current in registered)
        {
            if (baseline is not null && baseline != current)
            {
                throw new ConsulConfigurationException(ConsulConfigurationError.InvalidConfiguration);
            }

            baseline = current;
        }

        return baseline ?? throw new ConsulConfigurationException(ConsulConfigurationError.InvalidConfiguration);
    }

    /// <summary>
    /// Builds the accessor getter the client provider captures. The global accessor is resolved
    /// eagerly, exactly as before this selection existed; a typed accessor is resolved lazily
    /// inside <c>ConsulClientProvider.CreateClient</c>'s existing safe capture boundary, so
    /// registration, provider construction, and host builds never resolve it and a missing or
    /// throwing registration surfaces as the closed <see cref="ConsulConfigurationError.SnapshotUnavailable"/>
    /// category instead of a raw dependency exception.
    /// </summary>
    internal static Func<IServiceSettingCurrentSnapshotAccessor> ResolveAccessor(IServiceProvider provider)
    {
        var selection = Single(provider.GetServices<ConsulSnapshotSourceSelection>());
        if (selection.TypedAccessor is { } typed)
        {
            return () => (IServiceSettingCurrentSnapshotAccessor)provider.GetRequiredService(typed);
        }

        var global = provider.GetRequiredService<IServiceSettingCurrentSnapshotAccessor>();
        return () => global;
    }
}
