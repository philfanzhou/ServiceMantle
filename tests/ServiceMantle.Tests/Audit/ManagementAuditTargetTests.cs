using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditTargetTests
{
    [Fact]
    public void Create_trims_target_identifier()
    {
        var target = ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, "  signacore  ");

        Assert.Equal(WellKnownManagementAuditTargetTypes.Service, target.Type);
        Assert.Equal("signacore", target.Id);
    }

    [Fact]
    public void Create_rejects_empty_target_identifier()
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, "   "));

        Assert.Equal("audit.target_id_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_target_identifier_exceeding_max_length()
    {
        var tooLong = new string('a', ManagementAuditTarget.MaxTargetIdLength + 1);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Service, tooLong));

        Assert.Equal("audit.target_id_invalid", exception.ErrorCode);
    }

    [Fact]
    public void ToString_combines_type_and_identifier()
    {
        var target = ManagementAuditTarget.Create(WellKnownManagementAuditTargetTypes.Configuration, "smtp");

        Assert.Equal("configuration:smtp", target.ToString());
    }
}
