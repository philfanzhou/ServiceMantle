namespace ServiceMantle.Management;

/// <summary>
/// Fixed defaults of the ServiceMantle management identity contract.
/// </summary>
public static class ManagementIdentityDefaults
{
    /// <summary>
    /// The authentication type of every <see cref="System.Security.Claims.ClaimsIdentity"/> created
    /// by the ServiceMantle projection helper.
    /// </summary>
    /// <remarks>
    /// The parser does not require an external authentication handler to use this authentication
    /// type; authenticity of an externally produced principal stays with the consuming service.
    /// </remarks>
    public const string AuthenticationType = "ServiceMantle.Management";
}
