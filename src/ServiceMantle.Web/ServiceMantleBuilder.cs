using Microsoft.Extensions.DependencyInjection;

namespace ServiceMantle.Web;

/// <summary>
/// Exposes the service collection used to compose optional ServiceMantle host capabilities.
/// </summary>
public sealed class ServiceMantleBuilder
{
    internal ServiceMantleBuilder(IServiceCollection services)
    {
        Services = services;
    }

    /// <summary>
    /// Gets the service collection being configured.
    /// </summary>
    public IServiceCollection Services { get; }
}
