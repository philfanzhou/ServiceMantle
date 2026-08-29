using System.Security.Claims;
using ServiceMantle.Audit;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.Tests.Management;

public sealed class ManagementClaimsParserTests
{
    [Fact]
    public void NullPrincipalAndUnauthenticatedIdentity_AreUnauthenticatedRatherThanInvalid()
    {
        Assert.Equal(
            ManagementClaimsParseStatus.Unauthenticated,
            ManagementClaimsParser.Instance.Parse(null).Status);
        Assert.Equal(
            ManagementClaimsParseStatus.Unauthenticated,
            ManagementClaimsParser.Instance.Parse(new ClaimsPrincipal()).Status);

        // Claims without an authentication type are not authenticated, even when they are complete.
        var unauthenticated = new ClaimsPrincipal(new ClaimsIdentity(ValidClaims()));
        var result = ManagementClaimsParser.Instance.Parse(unauthenticated);

        Assert.Equal(ManagementClaimsParseStatus.Unauthenticated, result.Status);
        Assert.Null(result.ErrorCode);
        Assert.Null(result.Identity);
    }

    [Fact]
    public void CompletePrincipal_ResolvesTheOperatorAndIgnoresUnknownClaimTypes()
    {
        var claims = ValidClaims();
        claims.Add(new Claim("unrelated.claim", "ignored"));
        claims.Add(new Claim(ClaimTypes.Role, "administrator"));

        var result = ManagementClaimsParser.Instance.Parse(Authenticated(claims));

        Assert.Equal(ManagementClaimsParseStatus.Parsed, result.Status);
        Assert.Equal("admin-1", result.Identity!.OperatorId);
        Assert.Equal("interactive_admin", result.Identity.Source.Value);
        Assert.Equal("Ada Lovelace", result.Identity.DisplayName);
        Assert.Equal(
            new[] { ManagementPermission.Read, ManagementPermission.Admin },
            result.Identity.Permissions);
    }

    [Fact]
    public void UnauthenticatedSiblingIdentityWithoutManagementClaims_TakesNoPartInParsing()
    {
        var principal = Authenticated(ValidClaims());
        principal.AddIdentity(new ClaimsIdentity([new Claim("unrelated.claim", "ignored")]));

        Assert.Equal(
            ManagementClaimsParseStatus.Parsed,
            ManagementClaimsParser.Instance.Parse(principal).Status);
    }

    [Fact]
    public void MultipleAuthenticatedIdentities_FailClosed()
    {
        var principal = Authenticated(ValidClaims());
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim("unrelated.claim", "ignored")],
            "other-scheme"));

        AssertInvalid(principal, WellKnownManagementIdentityErrorCodes.IdentityAmbiguous);
    }

    [Fact]
    public void ManagementClaimsSplitAcrossIdentities_FailClosed()
    {
        var claims = ValidClaims();
        var permissionClaim = claims.Single(claim =>
            claim.Type == ManagementClaimTypes.Permission &&
            claim.Value == ManagementPermissions.AdminValue);
        claims.Remove(permissionClaim);

        var principal = Authenticated(claims);
        principal.AddIdentity(new ClaimsIdentity(
            [new Claim(ManagementClaimTypes.Permission, ManagementPermissions.AdminValue)]));

        AssertInvalid(principal, WellKnownManagementIdentityErrorCodes.ClaimsSplit);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicated")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("padded")]
    [InlineData("control-character")]
    [InlineData("too-long")]
    [InlineData("sensitive")]
    public void OperatorIdShapes_FailClosed(string shape)
    {
        var claims = ValidClaims();
        var operatorIdClaim = claims.Single(claim => claim.Type == ManagementClaimTypes.OperatorId);
        claims.Remove(operatorIdClaim);

        switch (shape)
        {
            case "missing":
                break;
            case "duplicated":
                claims.Add(new Claim(ManagementClaimTypes.OperatorId, "admin-1"));
                claims.Add(new Claim(ManagementClaimTypes.OperatorId, "admin-2"));
                break;
            case "empty":
                claims.Add(new Claim(ManagementClaimTypes.OperatorId, string.Empty));
                break;
            case "whitespace":
                claims.Add(new Claim(ManagementClaimTypes.OperatorId, "   "));
                break;
            case "padded":
                claims.Add(new Claim(ManagementClaimTypes.OperatorId, " admin-1 "));
                break;
            case "control-character":
                claims.Add(new Claim(ManagementClaimTypes.OperatorId, "admin\n1"));
                break;
            case "too-long":
                claims.Add(new Claim(
                    ManagementClaimTypes.OperatorId,
                    new string('a', ManagementAuditOperator.MaxOperatorIdLength + 1)));
                break;
            case "sensitive":
                claims.Add(new Claim(ManagementClaimTypes.OperatorId, "password=hunter2"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }

        AssertInvalid(
            Authenticated(claims),
            WellKnownManagementIdentityErrorCodes.OperatorIdInvalid);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("duplicated")]
    [InlineData("empty")]
    [InlineData("uppercase")]
    [InlineData("padded")]
    [InlineData("illegal")]
    public void OperatorSourceShapes_FailClosed(string shape)
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == ManagementClaimTypes.OperatorSource);

        switch (shape)
        {
            case "missing":
                break;
            case "duplicated":
                claims.Add(new Claim(ManagementClaimTypes.OperatorSource, "interactive_admin"));
                claims.Add(new Claim(ManagementClaimTypes.OperatorSource, "service_account"));
                break;
            case "empty":
                claims.Add(new Claim(ManagementClaimTypes.OperatorSource, string.Empty));
                break;
            case "uppercase":
                claims.Add(new Claim(ManagementClaimTypes.OperatorSource, "Interactive_Admin"));
                break;
            case "padded":
                claims.Add(new Claim(ManagementClaimTypes.OperatorSource, " interactive_admin "));
                break;
            case "illegal":
                claims.Add(new Claim(ManagementClaimTypes.OperatorSource, "interactive admin"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }

        AssertInvalid(
            Authenticated(claims),
            WellKnownManagementIdentityErrorCodes.OperatorSourceInvalid);
    }

    [Fact]
    public void OperatorSourceRule_DoesNotChangeTheGeneralAuditSourceNormalizationContract()
    {
        // The parser only accepts an already normalized wire value, but the shared audit parser keeps
        // normalizing casing and surrounding whitespace for every other caller.
        Assert.True(ManagementAuditOperatorSource.TryParse(" Interactive_Admin ", out var source));
        Assert.Equal("interactive_admin", source!.Value);
    }

    [Theory]
    [InlineData("duplicated")]
    [InlineData("empty")]
    [InlineData("whitespace")]
    [InlineData("padded")]
    [InlineData("sensitive")]
    public void DisplayNameShapes_FailClosed(string shape)
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == ManagementClaimTypes.OperatorDisplayName);

        switch (shape)
        {
            case "duplicated":
                claims.Add(new Claim(ManagementClaimTypes.OperatorDisplayName, "Ada Lovelace"));
                claims.Add(new Claim(ManagementClaimTypes.OperatorDisplayName, "Grace Hopper"));
                break;
            case "empty":
                claims.Add(new Claim(ManagementClaimTypes.OperatorDisplayName, string.Empty));
                break;
            case "whitespace":
                claims.Add(new Claim(ManagementClaimTypes.OperatorDisplayName, "  "));
                break;
            case "padded":
                claims.Add(new Claim(ManagementClaimTypes.OperatorDisplayName, " Ada Lovelace "));
                break;
            case "sensitive":
                claims.Add(new Claim(
                    ManagementClaimTypes.OperatorDisplayName,
                    "Ada Lovelace token=abcdef0123456789"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }

        AssertInvalid(
            Authenticated(claims),
            WellKnownManagementIdentityErrorCodes.DisplayNameInvalid);
    }

    [Fact]
    public void AbsentDisplayName_IsAccepted()
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == ManagementClaimTypes.OperatorDisplayName);

        var result = ManagementClaimsParser.Instance.Parse(Authenticated(claims));

        Assert.Equal(ManagementClaimsParseStatus.Parsed, result.Status);
        Assert.Null(result.Identity!.DisplayName);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("empty")]
    [InlineData("unknown")]
    [InlineData("duplicated")]
    [InlineData("uppercase")]
    [InlineData("padded")]
    public void PermissionShapes_FailClosed(string shape)
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == ManagementClaimTypes.Permission);

        switch (shape)
        {
            case "missing":
                break;
            case "empty":
                claims.Add(new Claim(ManagementClaimTypes.Permission, string.Empty));
                break;
            case "unknown":
                claims.Add(new Claim(ManagementClaimTypes.Permission, "management.superadmin"));
                break;
            case "duplicated":
                claims.Add(new Claim(ManagementClaimTypes.Permission, ManagementPermissions.AdminValue));
                claims.Add(new Claim(ManagementClaimTypes.Permission, ManagementPermissions.AdminValue));
                break;
            case "uppercase":
                claims.Add(new Claim(ManagementClaimTypes.Permission, "Management.Admin"));
                break;
            case "padded":
                claims.Add(new Claim(ManagementClaimTypes.Permission, " management.admin"));
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(shape));
        }

        AssertInvalid(
            Authenticated(claims),
            WellKnownManagementIdentityErrorCodes.PermissionInvalid);
    }

    [Fact]
    public void ClaimTypeNames_AreMatchedOrdinallyAndCaseSensitively()
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == ManagementClaimTypes.OperatorId);
        claims.Add(new Claim("ServiceMantle.Operator_Id", "admin-1"));

        AssertInvalid(
            Authenticated(claims),
            WellKnownManagementIdentityErrorCodes.OperatorIdInvalid);
    }

    [Fact]
    public void InvalidResults_CarryOnlyStableCodesAndNoClaimValues()
    {
        var claims = ValidClaims();
        claims.RemoveAll(claim => claim.Type == ManagementClaimTypes.OperatorId);
        claims.Add(new Claim(ManagementClaimTypes.OperatorId, "password=hunter2"));

        var result = ManagementClaimsParser.Instance.Parse(Authenticated(claims));

        Assert.Null(result.Identity);
        Assert.DoesNotContain("hunter2", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("hunter2", result.ErrorCode!, StringComparison.Ordinal);
        Assert.Contains(result.ErrorCode, StableErrorCodes());
    }

    [Fact]
    public void ErrorCodes_UseTheSafeAsciiShape()
    {
        Assert.All(StableErrorCodes(), code =>
        {
            Assert.InRange(code.Length, 1, 64);
            Assert.All(code, character => Assert.True(
                char.IsAsciiLetterOrDigit(character) || character is '.' or '_' or '-'));
        });

        Assert.Throws<ArgumentNullException>(() => ManagementClaimsParseResult.Invalid(null!));
        Assert.Throws<ArgumentException>(() => ManagementClaimsParseResult.Invalid(string.Empty));
        Assert.Throws<ArgumentException>(() => ManagementClaimsParseResult.Invalid("has space"));
        Assert.Throws<ArgumentException>(() =>
            ManagementClaimsParseResult.Invalid(new string('a', 65)));
    }

    internal static List<Claim> ValidClaims() =>
    [
        new(ManagementClaimTypes.OperatorId, "admin-1"),
        new(ManagementClaimTypes.OperatorSource, "interactive_admin"),
        new(ManagementClaimTypes.OperatorDisplayName, "Ada Lovelace"),
        new(ManagementClaimTypes.Permission, ManagementPermissions.ReadValue),
        new(ManagementClaimTypes.Permission, ManagementPermissions.AdminValue),
    ];

    internal static ClaimsPrincipal Authenticated(IEnumerable<Claim> claims) =>
        new(new ClaimsIdentity(claims, ManagementIdentityDefaults.AuthenticationType));

    private static string[] StableErrorCodes() =>
    [
        WellKnownManagementIdentityErrorCodes.ProviderFailed,
        WellKnownManagementIdentityErrorCodes.IdentityAmbiguous,
        WellKnownManagementIdentityErrorCodes.ClaimsSplit,
        WellKnownManagementIdentityErrorCodes.OperatorIdInvalid,
        WellKnownManagementIdentityErrorCodes.OperatorSourceInvalid,
        WellKnownManagementIdentityErrorCodes.DisplayNameInvalid,
        WellKnownManagementIdentityErrorCodes.PermissionInvalid,
    ];

    private static void AssertInvalid(ClaimsPrincipal principal, string expectedErrorCode)
    {
        var result = ManagementClaimsParser.Instance.Parse(principal);

        Assert.Equal(ManagementClaimsParseStatus.Invalid, result.Status);
        Assert.Equal(expectedErrorCode, result.ErrorCode);
        Assert.Null(result.Identity);
    }
}
