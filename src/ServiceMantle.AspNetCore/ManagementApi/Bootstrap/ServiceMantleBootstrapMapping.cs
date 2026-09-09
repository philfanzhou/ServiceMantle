using System.Runtime.CompilerServices;

namespace ServiceMantle.AspNetCore;

/// <summary>
/// Owns the endpoint-local mapping rules, fixed limits, and closed error codes of the Bootstrap
/// management entries.
/// </summary>
internal static class ServiceMantleBootstrapMapping
{
    /// <summary>The header the anonymous creation entry reads its one-time credential from.</summary>
    internal const string CredentialHeaderName = "X-ServiceMantle-Bootstrap-Credential";

    /// <summary>The single code every unusable, expired, replayed, or absent credential shares.</summary>
    internal const string CredentialInvalidErrorCode = "management.bootstrap.credential_invalid";

    /// <summary>The single code every store, validator, or internal failure shares.</summary>
    internal const string UnavailableErrorCode = "management.bootstrap.unavailable";

    /// <summary>The raw request body limit, counted in bytes before any decoding.</summary>
    internal const int MaximumBodyLength = 65536;

    /// <summary>The JSON nesting limit. The accepted document needs one object inside one object.</summary>
    internal const int MaximumJsonDepth = 8;

    /// <summary>
    /// The internal validator failures this group reports as a storage failure rather than as a
    /// rejected request, because none of them is evidence that the caller's values were wrong.
    /// </summary>
    private static readonly string[] UnavailableManagementErrorCodes =
    [
        "candidate.validation_failed",
        "candidate.invalid_result",
        "database.provider_invalid_result",
        "database.provider_validation_failed",
    ];

    /// <summary>
    /// Counts the mappings per host. The application's service provider is already built when an
    /// endpoint is mapped, so this per-host state cannot live in the container.
    /// </summary>
    private static readonly ConditionalWeakTable<IServiceProvider, StrongBox<int>> Mappings = new();

    internal static void RecordMap(IServiceProvider services)
    {
        var count = Mappings.GetValue(services, static _ => new StrongBox<int>(0));
        if (Interlocked.Increment(ref count.Value) != 1)
        {
            throw Failure();
        }
    }

    /// <summary>
    /// Reports whether a management failure code is a storage failure. Only the code is compared;
    /// it is never written to a response, a message, or a log.
    /// </summary>
    internal static bool IsUnavailableCode(string? errorCode) =>
        errorCode is not null &&
        Array.IndexOf(UnavailableManagementErrorCodes, errorCode) >= 0;

    internal static InvalidOperationException Failure() =>
        new("The ServiceMantle Bootstrap management entries must be mapped at most once.");

    internal static InvalidOperationException MissingCredentialStore() =>
        new("The ServiceMantle Bootstrap management entries require an explicitly registered " +
            "IBootstrapCredentialStore. ServiceMantle never provisions a credential on their " +
            "behalf and never configures its storage location from an HTTP request.");

    internal static InvalidOperationException MissingCapability() =>
        new("The ServiceMantle Bootstrap management entries require AddServiceMantle, " +
            "AddServiceMantleManagementApiV1, AddServiceMantleBootstrapManagement, the composed " +
            "ServiceMantle pipeline, and, for the update entry, the fixed management cookie " +
            "authentication scheme.");
}
