using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditTargetTypeTests
{
    [Fact]
    public void Parse_trims_and_normalizes_to_lowercase()
    {
        var targetType = ManagementAuditTargetType.Parse("  Service  ");

        Assert.Equal("service", targetType.Value);
    }

    [Fact]
    public void TryParse_rejects_invalid_characters()
    {
        Assert.False(ManagementAuditTargetType.TryParse("account/profile", out var targetType));
        Assert.Null(targetType);
    }

    [Fact]
    public void Parse_throws_management_audit_exception_with_stable_error_code()
    {
        var exception = Assert.Throws<ManagementAuditException>(() => ManagementAuditTargetType.Parse(""));

        Assert.Equal("audit.target_type_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Consuming_service_can_define_its_own_target_type()
    {
        var targetType = ManagementAuditTargetType.Parse("signacore.account");

        Assert.Equal("signacore.account", targetType.Value);
    }
}
