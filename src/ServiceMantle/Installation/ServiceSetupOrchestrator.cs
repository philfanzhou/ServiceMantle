namespace ServiceMantle.Installation;

/// <summary>
/// Validates every setup contributor before staging any contributor changes.
/// </summary>
/// <remarks>
/// This type cannot save or control a transaction. Success means only that every contributor has
/// staged its work; the caller remains responsible for saving and committing the shared unit of
/// work. After failure or cancellation, callers must not reuse a staging scope whose cleanliness
/// they cannot independently confirm.
/// </remarks>
public sealed class ServiceSetupOrchestrator
{
    private readonly IReadOnlyList<IServiceSetupContributor> contributors;
    private readonly IServiceSetupStagingScope stagingScope;

    /// <summary>Initializes an orchestrator and fixes contributor order.</summary>
    /// <exception cref="ArgumentException">Two contributors declare the same order.</exception>
    public ServiceSetupOrchestrator(
        IEnumerable<IServiceSetupContributor> contributors,
        IServiceSetupStagingScope stagingScope)
    {
        ArgumentNullException.ThrowIfNull(contributors);
        this.stagingScope = stagingScope ?? throw new ArgumentNullException(nameof(stagingScope));

        var materialized = contributors.ToArray();
        if (materialized.Any(static contributor => contributor is null))
        {
            throw new ArgumentException("Contributors must not contain null entries.", nameof(contributors));
        }

        OrderedContributor[] ordered;
        try
        {
            ordered = materialized
                .Select(static contributor => new OrderedContributor(contributor.Order, contributor))
                .OrderBy(static entry => entry.Order)
                .ToArray();
        }
        catch
        {
            throw new ArgumentException(
                "Contributor order could not be read.",
                nameof(contributors));
        }

        if (ordered.Zip(ordered.Skip(1), static (left, right) => left.Order == right.Order).Any(static duplicate => duplicate))
        {
            throw new ArgumentException("Contributor orders must be unique.", nameof(contributors));
        }

        this.contributors = ordered.Select(static entry => entry.Contributor).ToArray();
    }

    /// <summary>
    /// Validates all contributors, then stages all registrations in stable ascending order.
    /// After failure cleanup has settled, observed caller cancellation takes precedence over the
    /// failure classification and ends the orchestration with a safe
    /// <see cref="OperationCanceledException"/> carrying the caller's token.
    /// </summary>
    public async ValueTask<ServiceSetupResult> OrchestrateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (stagingScope.HasPendingChanges)
        {
            return ServiceSetupResult.Failure(WellKnownSetupCodeErrorCodes.DirtyContext);
        }

        foreach (var contributor in contributors)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
            }

            ServiceSetupContributorResult? result;
            try
            {
                result = await contributor.ValidateAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
                }

                return stagingScope.HasPendingChanges
                    ? await CleanupFailureAsync(WellKnownServiceSetupErrorCodes.ContributorFailed, cancellationToken)
                        .ConfigureAwait(false)
                    : ServiceSetupResult.Failure(WellKnownServiceSetupErrorCodes.ContributorFailed);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
            }

            if (stagingScope.HasPendingChanges)
            {
                return await CleanupFailureAsync(WellKnownServiceSetupErrorCodes.ValidationSideEffect, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (result is null || !result.Succeeded)
            {
                return ServiceSetupResult.Failure(
                    result?.ErrorCode ?? WellKnownServiceSetupErrorCodes.ContributorFailed);
            }
        }

        foreach (var contributor in contributors)
        {
            if (cancellationToken.IsCancellationRequested)
            {
                await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
            }

            ServiceSetupContributorResult? result;
            try
            {
                result = await contributor.RegisterAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
                throw;
            }
            catch
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
                }

                return await CleanupFailureAsync(WellKnownServiceSetupErrorCodes.ContributorFailed, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (cancellationToken.IsCancellationRequested)
            {
                await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
            }

            if (result is null)
            {
                return await CleanupFailureAsync(WellKnownServiceSetupErrorCodes.ContributorFailed, cancellationToken)
                    .ConfigureAwait(false);
            }

            if (!result.Succeeded)
            {
                return await CleanupFailureAsync(result.ErrorCode!, cancellationToken).ConfigureAwait(false);
            }
        }

        if (cancellationToken.IsCancellationRequested)
        {
            await CleanupBeforeCancellationAsync(cancellationToken).ConfigureAwait(false);
        }

        return ServiceSetupResult.Success();
    }

    /// <summary>
    /// Discards staged changes exactly once with <see cref="CancellationToken.None"/>, rechecks
    /// cleanliness, and classifies the failure. After the discard and the existing cleanliness
    /// recheck have settled, a completion checkpoint lets caller cancellation take precedence over
    /// the original rejection, contributor failure, validation side effect, and cleanup failure
    /// classifications. The final cancellation is delivered outside the exception normalization,
    /// so it can never be reclassified as a cleanup failure.
    /// </summary>
    private async ValueTask<ServiceSetupResult> CleanupFailureAsync(
        string errorCode,
        CancellationToken cancellationToken)
    {
        ServiceSetupResult failure;
        try
        {
            await stagingScope.DiscardPendingChangesAsync(CancellationToken.None).ConfigureAwait(false);
            failure = stagingScope.HasPendingChanges
                ? ServiceSetupResult.Failure(WellKnownServiceSetupErrorCodes.CleanupFailed)
                : ServiceSetupResult.Failure(errorCode);
        }
        catch
        {
            failure = ServiceSetupResult.Failure(WellKnownServiceSetupErrorCodes.CleanupFailed);
        }

        if (cancellationToken.IsCancellationRequested)
        {
            throw SafeCallerCancellation(cancellationToken);
        }

        return failure;
    }

    private static OperationCanceledException SafeCallerCancellation(CancellationToken cancellationToken) =>
        new("Service setup was cancelled by the caller.", cancellationToken);

    private async ValueTask CleanupBeforeCancellationAsync(CancellationToken cancellationToken)
    {
        try
        {
            await stagingScope.DiscardPendingChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch
        {
            // Caller cancellation remains authoritative; a failed cleanup must not expose internals.
        }

        throw new OperationCanceledException(cancellationToken);
    }

    private sealed record OrderedContributor(int Order, IServiceSetupContributor Contributor);
}
