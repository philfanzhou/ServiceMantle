using Microsoft.AspNetCore.Authorization;

namespace ServiceMantle.Management;

/// <summary>
/// Grants a <see cref="ManagementPermissionRequirement"/> only for an authenticated principal whose
/// ServiceMantle claims resolve to exactly one legitimate operator holding the required permission.
/// </summary>
/// <remarks>
/// The handler consumes nothing but the authentication conclusion and the legitimate claims of the
/// current principal; it never calls an <see cref="IManagementIdentityProvider"/> and never invents an
/// HTTP status code. A requirement that is not met is simply left unsucceeded, so the standard
/// ASP.NET Core authorization result applies.
/// </remarks>
public sealed class ManagementPermissionAuthorizationHandler
    : AuthorizationHandler<ManagementPermissionRequirement>
{
    private readonly IManagementCurrentOperatorResolver resolver;

    /// <summary>
    /// Initializes the handler.
    /// </summary>
    public ManagementPermissionAuthorizationHandler(IManagementCurrentOperatorResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        this.resolver = resolver;
    }

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ManagementPermissionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        var resolution = resolver.Resolve(context.User);
        if (resolution.Status == ManagementCurrentOperatorStatus.Resolved &&
            resolution.Identity!.HasPermission(requirement.Permission))
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
