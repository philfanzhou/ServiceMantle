using System.Linq;
using System.Reflection;
using Xunit;

namespace ServiceMantle.Tests;

public class SmokeTests
{
    [Fact]
    public void ServiceMantle_assembly_should_be_loadable_and_exposed_for_reference()
    {
        var serviceMantleAssembly = Assembly.Load("ServiceMantle");
        Assert.NotNull(serviceMantleAssembly);

        Assert.NotNull(serviceMantleAssembly!.GetType("ServiceMantle.AssemblyMarker"));
    }
}
