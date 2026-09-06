using Microsoft.AspNetCore.Authorization;

namespace ServiceMantle.Management;

/// <summary>
/// Requires a legitimate current management operator without requiring any permission.
/// </summary>
public sealed class ManagementSessionRequirement : IAuthorizationRequirement
{
    /// <summary>Returns a safe projection.</summary>
    public override string ToString() => "ManagementSessionRequirement()";
}

/// <summary>
/// Grants a <see cref="ManagementSessionRequirement"/> only for an authenticated principal whose
/// ServiceMantle claims resolve to exactly one legitimate operator.
/// </summary>
/// <remarks>
/// The handler consumes nothing but the authentication conclusion and the legitimate claims of the
/// current principal; it never calls an <see cref="IManagementIdentityProvider"/> and never invents
/// an HTTP status code. A requirement that is not met is left unsucceeded, so the standard
/// ASP.NET Core authorization result applies. An authenticated principal carrying unacceptable
/// ServiceMantle claims is rejected even though it would satisfy <c>RequireAuthenticatedUser</c>.
/// </remarks>
public sealed class ManagementSessionAuthorizationHandler
    : AuthorizationHandler<ManagementSessionRequirement>
{
    private readonly IManagementCurrentOperatorResolver resolver;

    /// <summary>Initializes the handler.</summary>
    public ManagementSessionAuthorizationHandler(IManagementCurrentOperatorResolver resolver)
    {
        ArgumentNullException.ThrowIfNull(resolver);
        this.resolver = resolver;
    }

    /// <inheritdoc />
    protected override Task HandleRequirementAsync(
        AuthorizationHandlerContext context,
        ManagementSessionRequirement requirement)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(requirement);

        if (resolver.Resolve(context.User).Status == ManagementCurrentOperatorStatus.Resolved)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}
