using System.Diagnostics;

namespace ServiceMantle.Health;

/// <summary>Well-known safe failures produced while combining readiness contributors.</summary>
public static class WellKnownServiceReadinessContributorErrorCodes
{
    /// <summary>A contributor failed, returned null, or otherwise violated its result contract.</summary>
    public const string ContributorFailed = "health.contributor_failed";

    /// <summary>The shared total contributor budget was exhausted.</summary>
    public const string ContributorTimeout = "health.contributor_timeout";
}

/// <summary>Provides one ordered, read-only business readiness decision.</summary>
public interface IServiceReadinessContributor
{
    /// <summary>Gets the unique stable order in which this contributor executes.</summary>
    int Order { get; }

    /// <summary>
    /// Evaluates business readiness from the exact immutable snapshot sampled for this request.
    /// </summary>
    ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
        ServiceHealthSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

/// <summary>Contains one immutable safe business readiness decision.</summary>
public sealed class ServiceReadinessContributorResult
{
    private ServiceReadinessContributorResult(bool isReady, string? errorCode)
    {
        IsReady = isReady;
        ErrorCode = errorCode;
    }

    /// <summary>Gets a value indicating whether the contributor accepted readiness.</summary>
    public bool IsReady { get; }

    /// <summary>Gets the stable safe rejection code, or null when ready.</summary>
    public string? ErrorCode { get; }

    /// <summary>Creates a ready decision.</summary>
    public static ServiceReadinessContributorResult Ready() => new(true, null);

    /// <summary>Creates a not-ready decision with one safe error code.</summary>
    public static ServiceReadinessContributorResult NotReady(string errorCode) =>
        new(false, ServiceHealthErrorCode.EnsureValid(errorCode, nameof(errorCode)));

    /// <summary>Returns only the finite decision and safe error code.</summary>
    public override string ToString() =>
        $"ServiceReadinessContributorResult(IsReady={IsReady}, ErrorCode={ErrorCode ?? "<none>"})";
}

/// <summary>
/// Executes validated readiness contributors in stable order under one shared total budget.
/// </summary>
public sealed class ServiceReadinessContributorCombiner
{
    private const string InvalidRegistrationMessage =
        "Service readiness contributor registration is invalid.";

    private readonly IReadOnlyList<OrderedContributor> contributors;

    /// <summary>Initializes and validates an immutable contributor sequence.</summary>
    /// <exception cref="InvalidOperationException">
    /// A contributor is null, its order cannot be read, or more than one contributor has the same order.
    /// </exception>
    public ServiceReadinessContributorCombiner(
        IEnumerable<IServiceReadinessContributor> contributors)
    {
        ArgumentNullException.ThrowIfNull(contributors);

        try
        {
            var ordered = new List<OrderedContributor>();
            var usedOrders = new HashSet<int>();
            foreach (var contributor in contributors)
            {
                if (contributor is null)
                {
                    throw new InvalidOperationException();
                }

                var order = contributor.Order;
                if (!usedOrders.Add(order))
                {
                    throw new InvalidOperationException();
                }

                ordered.Add(new OrderedContributor(order, contributor));
            }

            this.contributors = ordered
                .OrderBy(item => item.Order)
                .ToList()
                .AsReadOnly();
        }
        catch
        {
            throw new InvalidOperationException(InvalidRegistrationMessage);
        }
    }

    /// <summary>
    /// Evaluates all contributors sequentially and returns the lowest-order failure.
    /// </summary>
    /// <remarks>
    /// Rejections and contributor failures do not short-circuit. Exhausting the shared total budget
    /// returns <c>health.contributor_timeout</c>. Caller cancellation is propagated.
    /// </remarks>
    /// <exception cref="OperationCanceledException">The caller cancelled the evaluation.</exception>
    public async ValueTask<ServiceReadinessContributorResult> EvaluateAsync(
        ServiceHealthSnapshot snapshot,
        TimeSpan totalBudget,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        if (totalBudget <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(totalBudget));
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (contributors.Count == 0)
        {
            return ServiceReadinessContributorResult.Ready();
        }

        using var budgetCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budgetCancellation.CancelAfter(totalBudget);
        var elapsed = Stopwatch.StartNew();
        ServiceReadinessContributorResult? selectedFailure = null;

        foreach (var ordered in contributors)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = totalBudget - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return Timeout(cancellationToken);
            }

            ServiceReadinessContributorResult? result;
            try
            {
                result = await ordered.Contributor
                    .EvaluateAsync(snapshot, budgetCancellation.Token)
                    .AsTask()
                    .WaitAsync(remaining, cancellationToken)
                    .ConfigureAwait(false);
                cancellationToken.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException)
            {
                if (cancellationToken.IsCancellationRequested)
                {
                    budgetCancellation.Cancel();
                    throw new OperationCanceledException(cancellationToken);
                }

                if (budgetCancellation.IsCancellationRequested || elapsed.Elapsed >= totalBudget)
                {
                    return Timeout(cancellationToken);
                }

                selectedFailure ??= Failed();
                continue;
            }
            catch (TimeoutException)
            {
                budgetCancellation.Cancel();
                return Timeout(cancellationToken);
            }
            catch
            {
                cancellationToken.ThrowIfCancellationRequested();
                selectedFailure ??= Failed();
                continue;
            }

            if (budgetCancellation.IsCancellationRequested || elapsed.Elapsed >= totalBudget)
            {
                return Timeout(cancellationToken);
            }

            if (result is null)
            {
                selectedFailure ??= Failed();
            }
            else if (!result.IsReady)
            {
                selectedFailure ??= result;
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        return selectedFailure ?? ServiceReadinessContributorResult.Ready();
    }

    private static ServiceReadinessContributorResult Failed() =>
        ServiceReadinessContributorResult.NotReady(
            WellKnownServiceReadinessContributorErrorCodes.ContributorFailed);

    private static ServiceReadinessContributorResult Timeout(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ServiceReadinessContributorResult.NotReady(
            WellKnownServiceReadinessContributorErrorCodes.ContributorTimeout);
    }

    private sealed record OrderedContributor(int Order, IServiceReadinessContributor Contributor);
}
