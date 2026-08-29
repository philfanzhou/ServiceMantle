using System.Security.Claims;
using ServiceMantle.Audit;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.Tests.Management;

public sealed class ManagementIdentityContractTests
{
    [Fact]
    public void PublicConstants_AreFixedWireValues()
    {
        Assert.Equal("servicemantle.operator_id", ManagementClaimTypes.OperatorId);
        Assert.Equal("servicemantle.operator_source", ManagementClaimTypes.OperatorSource);
        Assert.Equal("servicemantle.operator_display_name", ManagementClaimTypes.OperatorDisplayName);
        Assert.Equal("servicemantle.permission", ManagementClaimTypes.Permission);

        Assert.Equal("management.read", ManagementPermissions.ReadValue);
        Assert.Equal("management.write", ManagementPermissions.WriteValue);
        Assert.Equal("management.admin", ManagementPermissions.AdminValue);
        Assert.Equal("ServiceMantle.Management", ManagementIdentityDefaults.AuthenticationType);

        Assert.Equal(
            new[] { ManagementPermission.Read, ManagementPermission.Write, ManagementPermission.Admin },
            ManagementPermissions.All);
        Assert.Equal(
            ManagementPermissions.All,
            Enum.GetValues<ManagementPermission>());
        Assert.All(
            ManagementPermissions.All,
            permission => Assert.True(ManagementPermissions.TryParse(
                ManagementPermissions.ToWireValue(permission),
                out var parsed) && parsed == permission));
    }

    [Theory]
    [InlineData("Management.Admin")]
    [InlineData("MANAGEMENT.ADMIN")]
    [InlineData(" management.admin")]
    [InlineData("management.admin ")]
    [InlineData("management.superadmin")]
    [InlineData("")]
    [InlineData(null)]
    public void PermissionWireValues_AreMatchedOrdinallyAndFailClosed(string? value)
    {
        Assert.False(ManagementPermissions.TryParse(value, out var permission));
        Assert.Equal(default, permission);
    }

    [Fact]
    public void Create_ValidatesOperatorIdentifierPermissionsAndDisplayName()
    {
        var source = WellKnownManagementAuditOperatorSources.InteractiveAdmin;

        Assert.Throws<ArgumentNullException>(() =>
            ManagementIdentity.Create(null!, "admin-1", [ManagementPermission.Admin]));
        Assert.Throws<ArgumentNullException>(() =>
            ManagementIdentity.Create(source, null!, [ManagementPermission.Admin]));
        Assert.Throws<ArgumentNullException>(() =>
            ManagementIdentity.Create(source, "admin-1", null!));
        Assert.Throws<ArgumentException>(() =>
            ManagementIdentity.Create(source, "   ", [ManagementPermission.Admin]));
        Assert.Throws<ArgumentException>(() =>
            ManagementIdentity.Create(source, "admin-1", []));
        Assert.Throws<ArgumentException>(() =>
            ManagementIdentity.Create(source, "admin-1", [(ManagementPermission)42]));
        Assert.Throws<ArgumentException>(() =>
            ManagementIdentity.Create(source, "admin-1", [ManagementPermission.Admin], "  "));
        Assert.Throws<ManagementAuditException>(() =>
            ManagementIdentity.Create(source, "password=hunter2", [ManagementPermission.Admin]));
    }

    [Fact]
    public void Create_CollapsesDuplicatesAndKeepsTheFixedPermissionOrder()
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Admin, ManagementPermission.Read, ManagementPermission.Admin]);

        Assert.Equal(
            new[] { ManagementPermission.Read, ManagementPermission.Admin },
            identity.Permissions);
        Assert.True(identity.HasPermission(ManagementPermission.Admin));
        Assert.False(identity.HasPermission(ManagementPermission.Write));
    }

    [Fact]
    public void ToAuditOperator_ProjectsLosslesslyOntoTheExistingAuditModel()
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Admin],
            "Ada Lovelace");

        var auditOperator = identity.ToAuditOperator();

        Assert.Equal("admin-1", auditOperator.OperatorId);
        Assert.Equal("Ada Lovelace", auditOperator.DisplayName);
        Assert.Same(WellKnownManagementAuditOperatorSources.InteractiveAdmin, auditOperator.Source);
        Assert.Same(auditOperator, identity.ToAuditOperator());
    }

    [Fact]
    public void ToString_NeverIncludesTheDisplayName()
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Admin],
            "Ada Lovelace");

        Assert.DoesNotContain("Ada Lovelace", identity.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("Ada Lovelace", identity.ToAuditOperator().ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public void ToClaimsPrincipal_EmitsExactlyOneStandardAuthenticatedIdentity()
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Admin, ManagementPermission.Read],
            "Ada Lovelace");

        var principal = identity.ToClaimsPrincipal();
        var claimsIdentity = Assert.Single(principal.Identities);

        Assert.True(claimsIdentity.IsAuthenticated);
        Assert.Equal(ManagementIdentityDefaults.AuthenticationType, claimsIdentity.AuthenticationType);
        Assert.All(claimsIdentity.Claims, claim =>
        {
            Assert.Equal(ClaimValueTypes.String, claim.ValueType);
            Assert.Equal(ClaimsIdentity.DefaultIssuer, claim.Issuer);
            Assert.Equal(ClaimsIdentity.DefaultIssuer, claim.OriginalIssuer);
        });
        Assert.Equal(
            new[]
            {
                (ManagementClaimTypes.OperatorId, "admin-1"),
                (ManagementClaimTypes.OperatorSource, "interactive_admin"),
                (ManagementClaimTypes.OperatorDisplayName, "Ada Lovelace"),
                (ManagementClaimTypes.Permission, ManagementPermissions.ReadValue),
                (ManagementClaimTypes.Permission, ManagementPermissions.AdminValue),
            },
            claimsIdentity.Claims.Select(claim => (claim.Type, claim.Value)));
        Assert.Empty(principal.FindAll(claimsIdentity.RoleClaimType));
    }

    [Fact]
    public void ToClaimsPrincipal_OmitsAnAbsentDisplayName()
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.ServiceAccount,
            "svc-1",
            [ManagementPermission.Read]);

        Assert.Empty(identity.ToClaimsPrincipal()
            .FindAll(ManagementClaimTypes.OperatorDisplayName));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("Ada Lovelace")]
    public void StandardPrincipal_RoundTripsLosslesslyThroughTheParser(string? displayName)
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Read, ManagementPermission.Write, ManagementPermission.Admin],
            displayName);

        var result = ManagementClaimsParser.Instance.Parse(identity.ToClaimsPrincipal());

        Assert.Equal(ManagementClaimsParseStatus.Parsed, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Equal(identity.OperatorId, result.Identity!.OperatorId);
        Assert.Equal(identity.DisplayName, result.Identity.DisplayName);
        Assert.Equal(identity.Source.Value, result.Identity.Source.Value);
        Assert.Equal(identity.Permissions, result.Identity.Permissions);
    }
}
