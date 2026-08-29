namespace ServiceMantle.Management;

/// <summary>
/// Resolves the current management operator identity from credentials the implementation owns.
/// </summary>
/// <remarks>
/// Implementations are normally registered per call scope and obtain their credentials from their own
/// injected request or credential accessor. ServiceMantle deliberately keeps request context objects,
/// tokens, cookies, and arbitrary credential objects out of this interface and prescribes no
/// credential source. Call implementations
/// through <see cref="ManagementIdentityProviderInvoker"/> so that unexpected exceptions become a
/// stable closed result.
/// </remarks>
public interface IManagementIdentityProvider
{
    /// <summary>
    /// Gets the closed three-state identity result for the current call.
    /// </summary>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    ValueTask<ManagementIdentityResult> GetIdentityAsync(
        CancellationToken cancellationToken = default);
}
