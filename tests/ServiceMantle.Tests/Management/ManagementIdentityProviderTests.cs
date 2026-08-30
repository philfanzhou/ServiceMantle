using System.Security.Claims;
using ServiceMantle.Audit;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.Tests.Management;

public sealed class ManagementIdentityProviderTests
{
    [Fact]
    public async Task Invoker_KeepsTheThreeProviderStatesDistinguishable()
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Admin]);

        var authenticated = await ManagementIdentityProviderInvoker.InvokeAsync(
            new DelegateProvider(_ => ValueTask.FromResult(
                ManagementIdentityResult.Authenticated(identity))),
            TestContext.Current.CancellationToken);
        var unauthenticated = await ManagementIdentityProviderInvoker.InvokeAsync(
            new DelegateProvider(_ => ValueTask.FromResult(
                ManagementIdentityResult.Unauthenticated())),
            TestContext.Current.CancellationToken);
        var failed = await ManagementIdentityProviderInvoker.InvokeAsync(
            new DelegateProvider(_ => ValueTask.FromResult(
                ManagementIdentityResult.Failed("upstream.unavailable"))),
            TestContext.Current.CancellationToken);

        Assert.Equal(ManagementIdentityStatus.Authenticated, authenticated.Status);
        Assert.Same(identity, authenticated.Identity);
        Assert.Null(authenticated.ErrorCode);
        Assert.Equal(ManagementIdentityStatus.Unauthenticated, unauthenticated.Status);
        Assert.Null(unauthenticated.ErrorCode);
        Assert.Equal(ManagementIdentityStatus.Failed, failed.Status);
        Assert.Equal("upstream.unavailable", failed.ErrorCode);
    }

    [Fact]
    public async Task Invoker_ConvertsUnexpectedExceptionsIntoProviderFailed()
    {
        var result = await ManagementIdentityProviderInvoker.InvokeAsync(
            new DelegateProvider(_ => throw new InvalidOperationException(
                "connection string Password=hunter2 refused by ldap://upstream")),
            TestContext.Current.CancellationToken);

        Assert.Equal(ManagementIdentityStatus.Failed, result.Status);
        Assert.Equal(WellKnownManagementIdentityErrorCodes.ProviderFailed, result.ErrorCode);
        Assert.Null(result.Identity);
        Assert.DoesNotContain("hunter2", result.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("ldap", result.ToString(), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Invoker_TreatsANullProviderResultAsProviderFailed()
    {
        var result = await ManagementIdentityProviderInvoker.InvokeAsync(
            new DelegateProvider(_ => ValueTask.FromResult<ManagementIdentityResult>(null!)),
            TestContext.Current.CancellationToken);

        Assert.Equal(WellKnownManagementIdentityErrorCodes.ProviderFailed, result.ErrorCode);
    }

    [Fact]
    public async Task Invoker_PropagatesCancellationOnlyWhenTheCallerTokenIsCancelled()
    {
        using var cancelled = new CancellationTokenSource();
        await cancelled.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await ManagementIdentityProviderInvoker.InvokeAsync(
                new DelegateProvider(token => throw new OperationCanceledException(token)),
                cancelled.Token));

        var selfCancelled = await ManagementIdentityProviderInvoker.InvokeAsync(
            new DelegateProvider(_ => throw new OperationCanceledException()),
            TestContext.Current.CancellationToken);

        Assert.Equal(ManagementIdentityStatus.Failed, selfCancelled.Status);
        Assert.Equal(WellKnownManagementIdentityErrorCodes.ProviderFailed, selfCancelled.ErrorCode);
    }

    [Fact]
    public async Task Invoker_PassesTheCallerTokenThroughAndRejectsANullProvider()
    {
        using var source = new CancellationTokenSource();
        CancellationToken observed = default;

        await ManagementIdentityProviderInvoker.InvokeAsync(
            new DelegateProvider(token =>
            {
                observed = token;
                return ValueTask.FromResult(ManagementIdentityResult.Unauthenticated());
            }),
            source.Token);

        Assert.Equal(source.Token, observed);
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await ManagementIdentityProviderInvoker.InvokeAsync(
                null!,
                TestContext.Current.CancellationToken));
    }

    [Fact]
    public void Results_RejectUnsafeErrorCodesAndKeepASafeProjection()
    {
        Assert.Throws<ArgumentNullException>(() => ManagementIdentityResult.Failed(null!));
        Assert.Throws<ArgumentException>(() => ManagementIdentityResult.Failed(string.Empty));
        Assert.Throws<ArgumentException>(() =>
            ManagementIdentityResult.Failed("upstream failure: Password=hunter2"));
        Assert.Throws<ArgumentException>(() =>
            ManagementIdentityResult.Failed(new string('a', 65)));
        Assert.Throws<ArgumentNullException>(() => ManagementIdentityResult.Authenticated(null!));

        Assert.Equal(64, ManagementIdentityResult.Failed(new string('a', 64)).ErrorCode!.Length);
        Assert.Equal(
            "ManagementIdentityResult(Status=Unauthenticated, ErrorCode=)",
            ManagementIdentityResult.Unauthenticated().ToString());
    }

    [Fact]
    public void Resolver_DistinguishesUnauthenticatedFromClaimsInvalid()
    {
        var resolver = new ManagementCurrentOperatorResolver(ManagementClaimsParser.Instance);
        var claims = ManagementClaimsParserTests.ValidClaims();
        claims.RemoveAll(claim => claim.Type == ManagementClaimTypes.Permission);

        var unauthenticated = resolver.Resolve(new ClaimsPrincipal());
        var claimsInvalid = resolver.Resolve(ManagementClaimsParserTests.Authenticated(claims));
        var resolved = resolver.Resolve(
            ManagementClaimsParserTests.Authenticated(ManagementClaimsParserTests.ValidClaims()));

        Assert.Equal(ManagementCurrentOperatorStatus.Unauthenticated, unauthenticated.Status);
        Assert.Null(unauthenticated.ErrorCode);
        Assert.Null(unauthenticated.Operator);

        Assert.Equal(ManagementCurrentOperatorStatus.ClaimsInvalid, claimsInvalid.Status);
        Assert.Equal(WellKnownManagementIdentityErrorCodes.PermissionInvalid, claimsInvalid.ErrorCode);
        Assert.Null(claimsInvalid.Operator);
        Assert.DoesNotContain("Ada Lovelace", claimsInvalid.ToString(), StringComparison.Ordinal);

        Assert.Equal(ManagementCurrentOperatorStatus.Resolved, resolved.Status);
        Assert.Equal("admin-1", resolved.Operator!.OperatorId);
        Assert.Equal("Ada Lovelace", resolved.Operator.DisplayName);
        Assert.Equal("interactive_admin", resolved.Operator.Source.Value);
        Assert.Throws<ArgumentNullException>(() => new ManagementCurrentOperatorResolver(null!));
    }

    [Theory]
    [InlineData("token123")]
    [InlineData("eyJhbGciOiJIUzI1NiJ9")]
    [InlineData("Ada.Lovelace")]
    [InlineData("management.read")]
    [InlineData("audit.operator_id_invalid")]
    public void ServiceMantleOwnedRejections_RefuseCodesOutsideTheClosedSet(string errorCode)
    {
        // A character-shape rule alone accepts credential fragments, claim values, and foreign
        // error codes. Rejecting a claims principal is a ServiceMantle-owned decision, so both
        // rejection results accept only the declared classifications.
        Assert.False(WellKnownManagementIdentityErrorCodes.IsDefined(errorCode));
        Assert.Throws<ArgumentException>(() => ManagementClaimsParseResult.Invalid(errorCode));
        Assert.Throws<ArgumentException>(() => ManagementCurrentOperatorResult.ClaimsInvalid(errorCode));
    }

    [Fact]
    public void ServiceMantleOwnedRejections_AcceptEveryDeclaredClassification()
    {
        foreach (var errorCode in ManagementClaimsParserTests.StableErrorCodes())
        {
            Assert.True(WellKnownManagementIdentityErrorCodes.IsDefined(errorCode));
            Assert.Equal(errorCode, ManagementClaimsParseResult.Invalid(errorCode).ErrorCode);
            Assert.Equal(
                errorCode,
                ManagementCurrentOperatorResult.ClaimsInvalid(errorCode).ErrorCode);
        }

        Assert.Throws<ArgumentNullException>(() => ManagementClaimsParseResult.Invalid(null!));
        Assert.Throws<ArgumentNullException>(() => ManagementCurrentOperatorResult.ClaimsInvalid(null!));
    }


    private sealed class DelegateProvider(
        Func<CancellationToken, ValueTask<ManagementIdentityResult>> callback)
        : IManagementIdentityProvider
    {
        public ValueTask<ManagementIdentityResult> GetIdentityAsync(
            CancellationToken cancellationToken = default) => callback(cancellationToken);
    }
}
