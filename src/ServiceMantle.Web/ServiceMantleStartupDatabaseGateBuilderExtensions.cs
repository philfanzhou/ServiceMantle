using ServiceMantle.Web;

namespace Microsoft.Extensions.DependencyInjection;

/// <summary>Caller-driven startup database gate registration on the ServiceMantle builder.</summary>
public static class ServiceMantleStartupDatabaseGateBuilderExtensions
{
    /// <summary>Registers the shared gate services without options or a hosted run.</summary>
    public static ServiceMantleBuilder AddServiceMantleStartupDatabaseGateServices(this ServiceMantleBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);
        builder.Services.AddServiceMantleStartupDatabaseGateServices();
        return builder;
    }
}
