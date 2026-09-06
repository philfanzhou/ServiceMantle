using Microsoft.AspNetCore.Http;
using ServiceMantle.AspNetCore;

namespace ServiceMantle.Management;

/// <summary>
/// The finite set of shared management entries served directly under the configured versioned root.
/// </summary>
/// <remarks>
/// An entry is an opt-in convention, not a handler. ServiceMantle fixes its path, methods, phase
/// admission, authentication rule, rate-limit policy, security headers, and unsafe-request guard;
/// the consuming service supplies the handler behind it.
/// </remarks>
public enum ServiceMantleManagementEntryKind
{
    /// <summary>Anonymous <c>GET</c>/<c>HEAD {versionedRoot}/status</c>, admitted in every phase.</summary>
    InstallationStatus,

    /// <summary>Anonymous <c>POST {versionedRoot}/bootstrap</c>, admitted only before configuration.</summary>
    BootstrapCreate,

    /// <summary>Administrator <c>PUT {versionedRoot}/bootstrap</c>, admitted only when ready.</summary>
    BootstrapUpdate,

    /// <summary>Anonymous <c>GET</c>/<c>HEAD {versionedRoot}/setup</c>, admitted while pending or completed.</summary>
    SetupStatus,

    /// <summary>Anonymous <c>POST {versionedRoot}/setup</c>, admitted while pending or completed.</summary>
    SetupComplete,

    /// <summary>Anonymous <c>POST {versionedRoot}/session/login</c>, admitted only when ready.</summary>
    SessionLogin,

    /// <summary>Session <c>POST {versionedRoot}/session/logout</c>, admitted only when ready.</summary>
    SessionLogout,

    /// <summary>Session <c>GET</c>/<c>HEAD {versionedRoot}/session</c>, admitted only when ready.</summary>
    CurrentSession,
}

/// <summary>
/// Defines the fixed path, method, authentication, and rate-limit baseline of every management entry.
/// </summary>
public static class ServiceMantleManagementEntryDefaults
{
    /// <summary>
    /// The header every unsafe management entry request must carry exactly once with the value
    /// <see cref="UnsafeRequestHeaderValue"/>.
    /// </summary>
    /// <remarks>
    /// Together with JSON-only parsing and the default <c>SameSite=Strict</c> management cookie this
    /// is a limited browser CSRF mitigation for this entry set only: it stops a simple cross-site
    /// HTML form from issuing a conforming request. It is not a CORS, trusted-origin, TLS, proxy, or
    /// global CSRF policy, and it does not protect against compromised same-origin script.
    /// </remarks>
    public const string UnsafeRequestHeaderName = "X-ServiceMantle-Request";

    /// <summary>The only accepted value of <see cref="UnsafeRequestHeaderName"/>.</summary>
    public const string UnsafeRequestHeaderValue = "1";

    /// <summary>The fixed root-relative path of the installation status entry.</summary>
    public const string StatusPath = "/status";

    /// <summary>The fixed root-relative path of both Bootstrap entries.</summary>
    public const string BootstrapPath = "/bootstrap";

    /// <summary>The fixed root-relative path of both Setup entries.</summary>
    public const string SetupPath = "/setup";

    /// <summary>The fixed root-relative path of the current-session entry.</summary>
    public const string SessionPath = "/session";

    /// <summary>The fixed root-relative path of the login entry.</summary>
    public const string SessionLoginPath = "/session/login";

    /// <summary>The fixed root-relative path of the logout entry.</summary>
    public const string SessionLogoutPath = "/session/logout";

    private static readonly string[] ReadMethods = [HttpMethods.Get, HttpMethods.Head];

    private static readonly string[] PostMethods = [HttpMethods.Post];

    private static readonly string[] PutMethods = [HttpMethods.Put];

    /// <summary>Returns the fixed definition of one defined entry kind.</summary>
    /// <param name="kind">The entry kind.</param>
    /// <exception cref="ArgumentOutOfRangeException">The kind is not a defined value.</exception>
    internal static ServiceMantleManagementEntryDefinition Get(ServiceMantleManagementEntryKind kind) =>
        kind switch
        {
            ServiceMantleManagementEntryKind.InstallationStatus => new(
                kind,
                StatusPath,
                ReadMethods,
                ServiceMantleManagementSurface.Status,
                AuthorizationPolicyName: null,
                ServiceMantleRateLimitingDefaults.ManagementPolicyName,
                AnonymousClientPartition: true,
                RequiresUnsafeRequestHeader: false),
            ServiceMantleManagementEntryKind.BootstrapCreate => new(
                kind,
                BootstrapPath,
                PostMethods,
                ServiceMantleManagementSurface.Bootstrap,
                AuthorizationPolicyName: null,
                ServiceMantleRateLimitingDefaults.SetupPolicyName,
                AnonymousClientPartition: false,
                RequiresUnsafeRequestHeader: true),
            ServiceMantleManagementEntryKind.BootstrapUpdate => new(
                kind,
                BootstrapPath,
                PutMethods,
                ServiceMantleManagementSurface.Bootstrap,
                ManagementAuthorizationDefaults.AdminPolicyName,
                ServiceMantleRateLimitingDefaults.ManagementPolicyName,
                AnonymousClientPartition: false,
                RequiresUnsafeRequestHeader: true),
            ServiceMantleManagementEntryKind.SetupStatus => new(
                kind,
                SetupPath,
                ReadMethods,
                ServiceMantleManagementSurface.Setup,
                AuthorizationPolicyName: null,
                ServiceMantleRateLimitingDefaults.SetupPolicyName,
                AnonymousClientPartition: false,
                RequiresUnsafeRequestHeader: false),
            ServiceMantleManagementEntryKind.SetupComplete => new(
                kind,
                SetupPath,
                PostMethods,
                ServiceMantleManagementSurface.Setup,
                AuthorizationPolicyName: null,
                ServiceMantleRateLimitingDefaults.SetupPolicyName,
                AnonymousClientPartition: false,
                RequiresUnsafeRequestHeader: true),
            ServiceMantleManagementEntryKind.SessionLogin => new(
                kind,
                SessionLoginPath,
                PostMethods,
                ServiceMantleManagementSurface.Management,
                AuthorizationPolicyName: null,
                ServiceMantleRateLimitingDefaults.SetupPolicyName,
                AnonymousClientPartition: false,
                RequiresUnsafeRequestHeader: true),
            ServiceMantleManagementEntryKind.SessionLogout => new(
                kind,
                SessionLogoutPath,
                PostMethods,
                ServiceMantleManagementSurface.Management,
                ManagementAuthorizationDefaults.SessionPolicyName,
                ServiceMantleRateLimitingDefaults.ManagementPolicyName,
                AnonymousClientPartition: false,
                RequiresUnsafeRequestHeader: true),
            ServiceMantleManagementEntryKind.CurrentSession => new(
                kind,
                SessionPath,
                ReadMethods,
                ServiceMantleManagementSurface.Management,
                ManagementAuthorizationDefaults.SessionPolicyName,
                ServiceMantleRateLimitingDefaults.ManagementPolicyName,
                AnonymousClientPartition: false,
                RequiresUnsafeRequestHeader: false),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

    /// <summary>Reports whether the method is one this entry set treats as unsafe.</summary>
    internal static bool IsUnsafeMethod(string method) =>
        !HttpMethods.IsGet(method) && !HttpMethods.IsHead(method) && !HttpMethods.IsOptions(method);
}

/// <summary>The fixed, immutable baseline of one management entry kind.</summary>
internal sealed record ServiceMantleManagementEntryDefinition(
    ServiceMantleManagementEntryKind Kind,
    string PathSuffix,
    IReadOnlyList<string> Methods,
    ServiceMantleManagementSurface Surface,
    string? AuthorizationPolicyName,
    string RateLimitPolicyName,
    bool AnonymousClientPartition,
    bool RequiresUnsafeRequestHeader)
{
    internal bool IsAnonymous => AuthorizationPolicyName is null;

    internal bool AllowsMethod(string method) =>
        Methods.Any(candidate => string.Equals(candidate, method, StringComparison.OrdinalIgnoreCase));
}

/// <summary>Marks an endpoint as exactly one shared ServiceMantle management entry.</summary>
internal sealed record ServiceMantleManagementEntryMetadata(ServiceMantleManagementEntryKind Kind);

/// <summary>Marks an entry that carries the fixed unsafe-request header guard.</summary>
internal sealed record ServiceMantleUnsafeRequestGuardMetadata;
