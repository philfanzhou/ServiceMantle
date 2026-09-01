namespace ServiceMantle.Installation;

/// <summary>
/// Contributes product-owned validation and registration to service setup.
/// </summary>
/// <remarks>
/// Contributors share the caller-owned unit of work represented by
/// <see cref="IServiceSetupStagingScope"/>. Validation must be read-only. Registration may stage
/// changes, but must not save, commit, or take ownership of the caller's transaction.
/// </remarks>
public interface IServiceSetupContributor
{
    /// <summary>Gets the stable order used for both validation and registration.</summary>
    int Order { get; }

    /// <summary>Validates this contributor without changing the staging scope.</summary>
    ValueTask<ServiceSetupContributorResult> ValidateAsync(
        CancellationToken cancellationToken = default);

    /// <summary>Stages this contributor's changes without saving or committing them.</summary>
    ValueTask<ServiceSetupContributorResult> RegisterAsync(
        CancellationToken cancellationToken = default);
}
