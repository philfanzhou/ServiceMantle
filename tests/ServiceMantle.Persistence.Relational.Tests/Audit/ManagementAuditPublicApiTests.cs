using ServiceMantle.Persistence.Relational.Mapping;
using ServiceMantle.Persistence.Relational.Stores;
using Xunit;

namespace ServiceMantle.Persistence.Relational.Tests.Audit;

public sealed class ManagementAuditPublicApiTests
{
    [Fact]
    public void Persistence_public_api_does_not_expose_a_mutable_audit_entity_or_dbset_contract()
    {
        var assembly = typeof(EfCoreManagementAuditWriter<>).Assembly;

        Assert.DoesNotContain(
            assembly.ExportedTypes,
            type => type.FullName ==
                "ServiceMantle.Persistence.Relational.ManagementAuditLogEntity");
        Assert.DoesNotContain(
            assembly.ExportedTypes,
            type => type.FullName ==
                "ServiceMantle.Persistence.Relational.IServiceMantleAuditDbContext");
    }
}
