using ServiceMantle.Bootstrap;

namespace ServiceMantle.Tests.Bootstrap;

/// <summary>
/// Shared bootstrap provider test double that records the database configuration it received,
/// so tests can assert that dispatch always carries the canonical provider id.
/// </summary>
internal sealed class FakeBootstrapProvider : IBootstrapDatabaseProvider
{
    public FakeBootstrapProvider(BootstrapDatabaseProviderDescriptor descriptor) =>
        Descriptor = descriptor;

    public BootstrapDatabaseProviderDescriptor Descriptor { get; }

    public BootstrapDatabaseConfiguration? LastValidated { get; private set; }

    public int CallCount { get; private set; }

    public ValueTask<BootstrapValidationResult> ValidateAsync(
        BootstrapDatabaseConfiguration database,
        CancellationToken cancellationToken)
    {
        CallCount++;
        LastValidated = database;
        return ValueTask.FromResult(BootstrapValidationResult.Success());
    }
}
