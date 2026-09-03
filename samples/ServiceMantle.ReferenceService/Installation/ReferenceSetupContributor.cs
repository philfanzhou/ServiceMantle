using ServiceMantle.Installation;
using ServiceMantle.ReferenceService.Configuration;
using ServiceMantle.ReferenceService.Data;

namespace ServiceMantle.ReferenceService.Installation;

/// <summary>A staging-only example; no setup orchestration invokes it during host startup.</summary>
public sealed class ReferenceSetupContributor(ReferenceDbContext context) : IServiceSetupContributor
{
    public int Order => 100;

    public ValueTask<ServiceSetupContributorResult> ValidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ServiceSetupContributorResult.Success());
    }

    public ValueTask<ServiceSetupContributorResult> RegisterAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Workspaces.Add(new ReferenceWorkspace { Id = Guid.NewGuid(), DisplayName = ReferenceSettingDefinitions.DefaultDisplayName });
        return ValueTask.FromResult(ServiceSetupContributorResult.Success());
    }
}
