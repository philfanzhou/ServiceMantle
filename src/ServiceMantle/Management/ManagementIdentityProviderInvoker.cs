namespace ServiceMantle.Management;

/// <summary>
/// Calls an <see cref="IManagementIdentityProvider"/> safely: every unexpected provider outcome
/// becomes the stable <see cref="WellKnownManagementIdentityErrorCodes.ProviderFailed"/> result.
/// </summary>
/// <remarks>
/// The invoker writes no logs and provides no timeout, retry, or circuit breaking.
/// </remarks>
public static class ManagementIdentityProviderInvoker
{
    /// <summary>
    /// Invokes a provider and normalizes its outcome.
    /// </summary>
    /// <param name="provider">The provider to call.</param>
    /// <param name="cancellationToken">The caller cancellation token.</param>
    /// <returns>The provider result, or a failed result carrying only a safe error code.</returns>
    /// <remarks>
    /// An <see cref="OperationCanceledException"/> propagates only when
    /// <paramref name="cancellationToken"/> has itself requested cancellation. A provider that
    /// cancels on its own, returns <see langword="null"/>, or throws anything else yields
    /// <see cref="WellKnownManagementIdentityErrorCodes.ProviderFailed"/>; no original exception or
    /// inner exception is retained.
    /// </remarks>
    public static async ValueTask<ManagementIdentityResult> InvokeAsync(
        IManagementIdentityProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);

        try
        {
            return await provider.GetIdentityAsync(cancellationToken).ConfigureAwait(false)
                ?? ManagementIdentityResult.Failed(
                    WellKnownManagementIdentityErrorCodes.ProviderFailed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return ManagementIdentityResult.Failed(
                WellKnownManagementIdentityErrorCodes.ProviderFailed);
        }
    }
}
