using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle.Audit;
using ServiceMantle.Management;
using Xunit;

namespace ServiceMantle.AspNetCore.Tests;

public sealed class ManagementAuthorizationTests
{
    [Fact]
    public void PolicyName_IsAFixedPublicConstant()
    {
        Assert.Equal("ServiceMantle.ManagementAdmin", ManagementAuthorizationDefaults.AdminPolicyName);
    }

    [Fact]
    public async Task AdminPolicy_RequiresAnAuthenticatedUserAndTheAdminPermission()
    {
        using var provider = BuildProvider();
        var policy = await provider
            .GetRequiredService<IAuthorizationPolicyProvider>()
            .GetPolicyAsync(ManagementAuthorizationDefaults.AdminPolicyName);

        Assert.NotNull(policy);
        Assert.Contains(policy.Requirements, requirement =>
            requirement is DenyAnonymousAuthorizationRequirement);
        Assert.Equal(
            ManagementPermission.Admin,
            Assert.Single(policy.Requirements.OfType<ManagementPermissionRequirement>()).Permission);
    }

    [Theory]
    [InlineData(false, ManagementPermission.Read)]
    [InlineData(false, ManagementPermission.Write)]
    [InlineData(true, ManagementPermission.Admin)]
    public async Task AdminPolicy_IsSatisfiedOnlyByTheAdminPermission(
        bool expected,
        ManagementPermission permission)
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [permission]);

        Assert.Equal(expected, await AuthorizeAsync(identity.ToClaimsPrincipal()));
    }

    [Fact]
    public async Task AdminPolicy_AllowsAdminToCarryTheOtherKnownPermissions()
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Read, ManagementPermission.Write, ManagementPermission.Admin]);

        Assert.True(await AuthorizeAsync(identity.ToClaimsPrincipal()));
    }

    [Fact]
    public async Task AdminPolicy_DeniesUnauthenticatedAndClaimsInvalidPrincipals()
    {
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Admin]);
        var unauthenticated = new ClaimsPrincipal(
            new ClaimsIdentity(identity.ToClaimsIdentity().Claims));
        var splitAcrossIdentities = identity.ToClaimsPrincipal();
        splitAcrossIdentities.AddIdentity(new ClaimsIdentity(
            [new Claim(ManagementClaimTypes.Permission, ManagementPermissions.AdminValue)]));
        var duplicateOperator = new ClaimsPrincipal(new ClaimsIdentity(
            [
                .. identity.ToClaimsIdentity().Claims,
                new Claim(ManagementClaimTypes.OperatorId, "admin-2"),
            ],
            ManagementIdentityDefaults.AuthenticationType));

        Assert.False(await AuthorizeAsync(new ClaimsPrincipal()));
        Assert.False(await AuthorizeAsync(unauthenticated));
        Assert.False(await AuthorizeAsync(splitAcrossIdentities));
        Assert.False(await AuthorizeAsync(duplicateOperator));
    }

    [Fact]
    public async Task Registration_IsIdempotentAndStandalone()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddServiceMantleManagementAuthorization();
        services.AddServiceMantleManagementAuthorization();

        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IManagementClaimsParser));
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IManagementCurrentOperatorResolver));
        Assert.Single(
            services,
            descriptor => descriptor.ServiceType == typeof(IAuthorizationHandler) &&
                descriptor.ImplementationType == typeof(ManagementPermissionAuthorizationHandler));
        Assert.DoesNotContain(
            services,
            descriptor => descriptor.ServiceType == typeof(IManagementIdentityProvider));

        using var provider = services.BuildServiceProvider();
        var identity = ManagementIdentity.Create(
            WellKnownManagementAuditOperatorSources.InteractiveAdmin,
            "admin-1",
            [ManagementPermission.Admin]);

        Assert.True((await provider
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(
                identity.ToClaimsPrincipal(),
                ManagementAuthorizationDefaults.AdminPolicyName)).Succeeded);
        Assert.Throws<ArgumentNullException>(() =>
            ServiceMantleManagementAuthorizationServiceCollectionExtensions
                .AddServiceMantleManagementAuthorization(null!));
    }

    [Fact]
    public void Requirement_RejectsUndefinedPermissionsAndKeepsASafeProjection()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ManagementPermissionRequirement((ManagementPermission)42));
        Assert.Equal(
            "ManagementPermissionRequirement(Permission=Admin)",
            new ManagementPermissionRequirement(ManagementPermission.Admin).ToString());
        Assert.Throws<ArgumentNullException>(() =>
            new ManagementPermissionAuthorizationHandler(null!));
    }

    [Fact]
    public async Task MinimalHost_StartsWithoutAnAuthenticationSchemeOrIdentityProvider()
    {
        var builder = WebApplication.CreateSlimBuilder();
        builder.WebHost.UseUrls("http://127.0.0.1:0");
        builder.Services.AddServiceMantleManagementAuthorization();

        await using var app = builder.Build();
        app.Run(context => context.Response.WriteAsync("ready"));
        await app.StartAsync(TestContext.Current.CancellationToken);

        using var client = new HttpClient();
        Assert.Equal(
            "ready",
            await client.GetStringAsync(app.Urls.First(), TestContext.Current.CancellationToken));
        Assert.Null(app.Services.GetService<IManagementIdentityProvider>());

        await app.StopAsync(TestContext.Current.CancellationToken);
    }

    private static async Task<bool> AuthorizeAsync(ClaimsPrincipal principal)
    {
        using var provider = BuildProvider();
        var result = await provider
            .GetRequiredService<IAuthorizationService>()
            .AuthorizeAsync(principal, ManagementAuthorizationDefaults.AdminPolicyName);
        return result.Succeeded;
    }

    private static ServiceProvider BuildProvider() =>
        new ServiceCollection()
            .AddLogging()
            .AddServiceMantleManagementAuthorization()
            .BuildServiceProvider();
}
