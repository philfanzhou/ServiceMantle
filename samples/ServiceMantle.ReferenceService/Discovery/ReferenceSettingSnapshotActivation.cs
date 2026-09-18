using ServiceMantle.Configuration;

namespace ServiceMantle.ReferenceService.Discovery;

/// <summary>
/// Activates the first setting snapshot during startup, before any capability that reads settings
/// from the snapshot - the Consul registration lifecycle - resolves its session.
/// </summary>
/// <remarks>
/// <para>
/// The step runs after the PostgreSQL gate's own hosted service (the registration order fixes
/// that), so the settings rows it reads belong to a migrated database. It performs exactly one
/// <see cref="ServiceSettingSnapshotLoader.RefreshAsync"/>: every <c>discovery.*</c> definition is
/// <c>requiresRestart</c>, so there is deliberately no background refresh and no hot reload.
/// </para>
/// <para>
/// A failed activation fails the startup with one fixed message that names only the safe error
/// codes - never a key, a value, or a connection detail. A caller cancellation propagates with its
/// own token and stops the startup.
/// </para>
/// </remarks>
public sealed class ReferenceSettingSnapshotActivation(ServiceSettingSnapshotLoader loader)
    : IHostedLifecycleService
{
    /// <inheritdoc />
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var result = await loader.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(
                "The reference service setting snapshot could not be activated: " +
                string.Join(", ", result.Errors.Select(error => error.ErrorCode)) + ".");
        }
    }

    /// <inheritdoc />
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StartedAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppingAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <inheritdoc />
    public Task StoppedAsync(CancellationToken cancellationToken) => Task.CompletedTask;
}
