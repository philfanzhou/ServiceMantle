using ServiceMantle.Installation;
using ServiceMantle.ReferenceService.Configuration;
using ServiceMantle.ReferenceService.Data;
using ServiceMantle.ReferenceService.Database.PostgreSql;

namespace ServiceMantle.ReferenceService.Installation.PostgreSql;

/// <summary>
/// A consumer-owned example of the business half of setup staging on PostgreSQL: it stages one
/// workspace row on the caller's own context and does nothing else.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ValidateAsync"/> is read-only. It checks the caller's cancellation and nothing more:
/// it changes no tracked entity, runs no SQL, and reads no database state.
/// <see cref="RegisterAsync"/> stages exactly one <see cref="ReferenceWorkspace"/> with a new
/// <see cref="Guid"/> and the sample's existing default display name. It takes no product input, no
/// secret, and no Setup Code, and it never calls <c>SaveChanges</c>, opens a transaction, commits,
/// rolls back, or disposes the context.
/// </para>
/// <para>
/// Staging a row is not installing a service. This contributor writes no installation state,
/// completes no Setup, and provides no idempotence: two explicit registrations stage two rows with
/// two different ids. The caller owns the context, the save, the transaction, and the decision that
/// this call was authorized at all.
/// </para>
/// <para>
/// One context is used by one call at a time. The caller must hand this contributor a dedicated,
/// clean scope, and must discard the whole scope whenever it cannot establish that the context is
/// clean.
/// </para>
/// </remarks>
public sealed class ReferencePostgreSqlSetupContributor : IServiceSetupContributor
{
    private readonly ReferencePostgreSqlDbContext context;

    /// <summary>Creates the contributor over the caller's own context.</summary>
    /// <param name="context">
    /// The caller's context. It owns every save, every transaction, and this context's lifetime.
    /// </param>
    /// <exception cref="ArgumentNullException"><paramref name="context"/> is null.</exception>
    public ReferencePostgreSqlSetupContributor(ReferencePostgreSqlDbContext context)
    {
        ArgumentNullException.ThrowIfNull(context);
        this.context = context;
    }

    /// <inheritdoc />
    public int Order => 100;

    /// <inheritdoc />
    /// <remarks>Read-only: it observes the caller's cancellation and stages nothing.</remarks>
    public ValueTask<ServiceSetupContributorResult> ValidateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(ServiceSetupContributorResult.Success());
    }

    /// <inheritdoc />
    /// <remarks>
    /// Stages one workspace on the caller's context. Nothing is saved, and nothing reaches the
    /// database until the caller saves and commits.
    /// </remarks>
    public ValueTask<ServiceSetupContributorResult> RegisterAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        context.Workspaces.Add(new ReferenceWorkspace
        {
            Id = Guid.NewGuid(),
            DisplayName = ReferenceSettingDefinitions.DefaultDisplayName,
        });
        return ValueTask.FromResult(ServiceSetupContributorResult.Success());
    }
}
