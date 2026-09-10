using Microsoft.AspNetCore.Http;
using ServiceMantle.Configuration;

namespace ServiceMantle.AspNetCore.ManagementApi.SettingUpdates;

/// <summary>
/// Executes one validated management setting update inside a consumer-owned unit of work.
/// </summary>
/// <param name="httpContext">The current management HTTP request.</param>
/// <param name="command">The validated update command with the resolved current operator.</param>
/// <param name="cancellationToken">The request cancellation token.</param>
/// <returns>The closed update result after the consumer-owned transaction has completed.</returns>
/// <remarks>
/// The delegate must resolve a fresh scoped unit of work, begin its transaction, invoke the existing
/// <c>ServiceSettingUpdateService</c>, and commit only an applied result. It returns Applied only
/// after commit completes. Every failure or cancellation rolls back and discards that scope without
/// retrying. ServiceMantle does not implicitly commit an existing consumer unit of work, and a
/// malformed or malicious delegate is outside the endpoint guarantee.
/// </remarks>
public delegate ValueTask<ServiceSettingUpdateResult> SettingUpdateExecutor(
    HttpContext httpContext,
    ServiceSettingUpdateCommand command,
    CancellationToken cancellationToken);
