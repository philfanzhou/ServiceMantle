using ServiceMantle.Health;

namespace ServiceMantle.ReferenceService.Health;

/// <summary>Remains closed until the separate health integration task supplies a business check.</summary>
public sealed class ReferenceReadinessContributor : IServiceReadinessContributor
{
    public int Order => 100;
    public ValueTask<ServiceReadinessContributorResult> EvaluateAsync(ServiceHealthSnapshot snapshot, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ServiceReadinessContributorResult.NotReady("reference.health_not_integrated"));
    }
}
