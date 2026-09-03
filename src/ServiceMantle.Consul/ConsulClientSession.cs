namespace ServiceMantle.Consul;

/// <summary>Owns one snapshot-bound client and projects all transport failures to safe results.</summary>
/// <remarks>Dispose after all operations finish. Disposing does not deregister the service.</remarks>
public sealed class ConsulClientSession : IDisposable
{
    private readonly IConsulClient client;
    internal ConsulClientSession(IConsulClient client, ConsulServiceRegistration registration, long version)
    {
        this.client = client;
        Registration = registration;
        SnapshotVersion = version;
    }
    /// <summary>Gets the credential-free immutable registration.</summary>
    public ConsulServiceRegistration Registration { get; }
    /// <summary>Gets the captured snapshot version; later refreshes do not mutate this session.</summary>
    public long SnapshotVersion { get; }
    /// <summary>Explicitly registers once; does not check readiness, retry, or schedule updates.</summary>
    public ValueTask<ConsulClientResult> RegisterAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(true, cancellationToken);
    /// <summary>Explicitly deregisters once; does not retry or change local readiness.</summary>
    public ValueTask<ConsulClientResult> DeregisterAsync(CancellationToken cancellationToken = default) =>
        InvokeAsync(false, cancellationToken);

    private async ValueTask<ConsulClientResult> InvokeAsync(bool register, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = register
                ? await client.RegisterAsync(Registration, cancellationToken).ConfigureAwait(false)
                : await client.DeregisterAsync(Registration.Id, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return Enum.IsDefined(result) ? result : ConsulClientResult.Unavailable;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("The Consul operation was cancelled by the caller.", cancellationToken);
        }
        catch
        {
            // Cancellation takes precedence even if a transport uses a different exception type.
            if (cancellationToken.IsCancellationRequested)
            {
                throw new OperationCanceledException("The Consul operation was cancelled by the caller.", cancellationToken);
            }
            return ConsulClientResult.Unavailable;
        }
    }

    /// <inheritdoc />
    public void Dispose()
    {
        try { client.Dispose(); }
        catch { throw new ConsulConfigurationException(ConsulConfigurationError.ClientDisposalFailed); }
    }
    /// <summary>Returns version metadata only.</summary>
    public override string ToString() => $"ConsulClientSession(Version={SnapshotVersion})";
}
