using Microsoft.AspNetCore.Mvc;

namespace ServiceMantle.AspNetCore.Tests;

/// <summary>
/// Attribute-routed controllers the phase gate tests map into a host. An MVC controller has to be a
/// top-level public type to be discovered, and the combined template it produces never carries the
/// leading slash even though it stays root relative.
/// </summary>
[ApiController]
[Route("/api/admin")]
public sealed class BusinessSessionController : ControllerBase
{
    [HttpGet("session/login")]
    public string Login() => "login";

    [HttpPost("session/logout")]
    public string Logout() => "logout";
}

/// <summary>An attribute-routed controller that sits inside the management prefix without a surface.</summary>
[ApiController]
[Route("/management")]
public sealed class UnmarkedManagementController : ControllerBase
{
    [HttpGet("unclassified")]
    public string Unclassified() => "unclassified";
}
