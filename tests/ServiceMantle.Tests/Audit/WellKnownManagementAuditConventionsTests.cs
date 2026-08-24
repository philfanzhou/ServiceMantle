using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class WellKnownManagementAuditConventionsTests
{
    [Fact]
    public void Well_known_actions_are_valid_and_stable()
    {
        Assert.Equal("installation.completed", WellKnownManagementAuditActions.InstallationCompleted.Value);
        Assert.Equal("admin_login.succeeded", WellKnownManagementAuditActions.AdminLoginSucceeded.Value);
        Assert.Equal("admin_login.failed", WellKnownManagementAuditActions.AdminLoginFailed.Value);
        Assert.Equal("admin_login.logout", WellKnownManagementAuditActions.AdminLogout.Value);
        Assert.Equal("configuration.changed", WellKnownManagementAuditActions.ConfigurationChanged.Value);
    }

    [Fact]
    public void Well_known_target_types_are_valid_and_stable()
    {
        Assert.Equal("service", WellKnownManagementAuditTargetTypes.Service.Value);
        Assert.Equal("admin_session", WellKnownManagementAuditTargetTypes.AdminSession.Value);
        Assert.Equal("configuration", WellKnownManagementAuditTargetTypes.Configuration.Value);
    }

    [Fact]
    public void Well_known_operator_sources_are_valid_and_stable()
    {
        Assert.Equal("system", WellKnownManagementAuditOperatorSources.System.Value);
        Assert.Equal("interactive_admin", WellKnownManagementAuditOperatorSources.InteractiveAdmin.Value);
        Assert.Equal("service_account", WellKnownManagementAuditOperatorSources.ServiceAccount.Value);
        Assert.Equal("anonymous", WellKnownManagementAuditOperatorSources.Anonymous.Value);
    }

    [Fact]
    public void Consuming_service_can_extend_actions_alongside_well_known_ones()
    {
        var custom = ManagementAuditAction.Parse("signacore.account_created");

        Assert.NotEqual(WellKnownManagementAuditActions.ConfigurationChanged, custom);
        Assert.Equal("signacore.account_created", custom.Value);
    }
}
