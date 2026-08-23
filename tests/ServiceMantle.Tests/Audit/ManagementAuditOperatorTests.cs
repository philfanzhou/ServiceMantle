using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditOperatorTests
{
    [Fact]
    public void Create_retains_operator_id_and_display_name()
    {
        var operatorInfo = ManagementAuditOperator.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            operatorId: "admin-42",
            displayName: "Alex Admin");

        Assert.Equal("admin-42", operatorInfo.OperatorId);
        Assert.Equal("Alex Admin", operatorInfo.DisplayName);
        Assert.Equal(WellKnownManagementAuditOperatorSources.InteractiveAdmin, operatorInfo.Source);
    }

    [Fact]
    public void Create_allows_null_operator_id_and_display_name()
    {
        var operatorInfo = ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.System);

        Assert.Null(operatorInfo.OperatorId);
        Assert.Null(operatorInfo.DisplayName);
    }

    [Fact]
    public void System_factory_uses_system_source_and_no_operator_id()
    {
        var operatorInfo = ManagementAuditOperator.System();

        Assert.Equal(WellKnownManagementAuditOperatorSources.System, operatorInfo.Source);
        Assert.Null(operatorInfo.OperatorId);
    }

    [Fact]
    public void Create_rejects_operator_id_exceeding_max_length()
    {
        var tooLong = new string('a', ManagementAuditOperator.MaxOperatorIdLength + 1);

        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditOperator.Create(WellKnownManagementAuditOperatorSources.System, operatorId: tooLong));

        Assert.Equal("audit.operator_id_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_rejects_sensitive_content_in_operator_id()
    {
        var exception = Assert.Throws<ManagementAuditException>(() =>
            ManagementAuditOperator.Create(
                WellKnownManagementAuditOperatorSources.InteractiveAdmin,
                operatorId: "token=operator-secret"));

        Assert.Equal("audit.operator_id_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Create_redacts_sensitive_content_in_display_name()
    {
        var operatorInfo = ManagementAuditOperator.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            operatorId: "admin-42",
            displayName: "password: display-secret");

        Assert.DoesNotContain("display-secret", operatorInfo.DisplayName, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", operatorInfo.DisplayName, StringComparison.Ordinal);
    }

    [Fact]
    public void ToString_never_includes_display_name()
    {
        var operatorInfo = ManagementAuditOperator.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            operatorId: "admin-42",
            displayName: "Secret Real Name");

        Assert.DoesNotContain("Secret Real Name", operatorInfo.ToString(), StringComparison.Ordinal);
    }
}
