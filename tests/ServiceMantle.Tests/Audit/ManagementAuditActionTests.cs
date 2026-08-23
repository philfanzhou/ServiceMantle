using ServiceMantle.Audit;
using Xunit;

namespace ServiceMantle.Tests.Audit;

public sealed class ManagementAuditActionTests
{
    [Fact]
    public void Parse_trims_and_normalizes_to_lowercase()
    {
        var action = ManagementAuditAction.Parse("  Installation.Completed  ");

        Assert.Equal("installation.completed", action.Value);
        Assert.Equal("installation.completed", action.ToString());
    }

    [Fact]
    public void Parse_accepts_namespace_separator_characters()
    {
        var action = ManagementAuditAction.Parse("signacore:admin_login.succeeded-v2");

        Assert.Equal("signacore:admin_login.succeeded-v2", action.Value);
    }

    [Fact]
    public void TryParse_rejects_empty_value()
    {
        Assert.False(ManagementAuditAction.TryParse("", out var action));
        Assert.Null(action);
    }

    [Fact]
    public void TryParse_rejects_invalid_leading_character()
    {
        Assert.False(ManagementAuditAction.TryParse(".started", out var action));
        Assert.Null(action);
    }

    [Fact]
    public void TryParse_rejects_internal_spaces()
    {
        Assert.False(ManagementAuditAction.TryParse("account created", out var action));
        Assert.Null(action);
    }

    [Fact]
    public void TryParse_rejects_value_exceeding_max_length()
    {
        var tooLong = new string('a', ManagementAuditAction.MaxLength + 1);

        Assert.False(ManagementAuditAction.TryParse(tooLong, out var action));
        Assert.Null(action);
    }

    [Fact]
    public void Parse_throws_management_audit_exception_with_stable_error_code()
    {
        var exception = Assert.Throws<ManagementAuditException>(() => ManagementAuditAction.Parse("!bad"));

        Assert.Equal("audit.action_invalid", exception.ErrorCode);
    }

    [Fact]
    public void Equivalent_normalized_values_are_equal()
    {
        var first = ManagementAuditAction.Parse("Admin_Login.Succeeded");
        var second = ManagementAuditAction.Parse(" admin_login.succeeded ");

        Assert.Equal(first, second);
    }
}
