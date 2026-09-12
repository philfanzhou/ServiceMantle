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
    /// A provider is not called when <paramref name="cancellationToken"/> already requests
    /// cancellation. When the caller token requests cancellation at the entry point, at the point
    /// where the provider call settles, or when any provider exception arrives, the invocation
    /// ends in a new <see cref="OperationCanceledException"/> carrying the caller token, a fixed
    /// message, and no inner exception, instead of the provider outcome. A provider that cancels
    /// on its own, returns <see langword="null"/>, or throws anything else while the caller has
    /// not cancelled yields <see cref="WellKnownManagementIdentityErrorCodes.ProviderFailed"/>;
    /// no original exception or inner exception is retained.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller requested cancellation.</exception>
    public static async ValueTask<ManagementIdentityResult> InvokeAsync(
        IManagementIdentityProvider provider,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(provider);
        cancellationToken.ThrowIfCancellationRequested();

        try
        {
            var result = await provider.GetIdentityAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return result ?? ManagementIdentityResult.Failed(
                WellKnownManagementIdentityErrorCodes.ProviderFailed);
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(
                "The management identity call was cancelled by the caller.",
                cancellationToken);
        }
        catch (Exception)
        {
            return ManagementIdentityResult.Failed(
                WellKnownManagementIdentityErrorCodes.ProviderFailed);
        }
    }
}
