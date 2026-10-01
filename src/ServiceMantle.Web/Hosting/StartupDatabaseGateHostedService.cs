using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using ServiceMantle;
using ServiceMantle.Migration;

namespace ServiceMantle.Web.Hosting;

/// <summary>
/// Runs the <see cref="StartupDatabaseGate"/> during host startup, before any hosted service -
/// including the web host - starts, so the host never accepts a request before the gate
/// completed successfully.
/// </summary>
/// <remarks>
/// The hosted entry and any direct caller-driven invocation share the same
/// <see cref="StartupDatabaseGate"/> implementation and therefore the same ordering, failure,
/// and error-code semantics. A failed gate fails the host startup with a finite message that
/// carries only the safe error code recorded on the receipt; connection strings, host names, and
/// driver messages are never projected. Caller cancellation stops the host startup with the
/// caller's own token.
/// </remarks>
public sealed class StartupDatabaseGateHostedService : IHostedLifecycleService
{
    private readonly StartupDatabaseGate gate;
    private readonly StartupDatabaseGateOptions options;
    private readonly StartupDatabaseReceipt receipt;
    private readonly ServiceId serviceId;
    private readonly ILogger<StartupDatabaseGateHostedService> logger;

    /// <summary>Creates the hosted startup gate entry.</summary>
    /// <param name="gate">The shared gate implementation.</param>
    /// <param name="options">The immutable gate inputs.</param>
    /// <param name="receipt">The receipt the gate records.</param>
    /// <param name="serviceId">The identity shared by all instances of the service.</param>
    /// <param name="logger">The logger; messages never contain connection values.</param>
    public StartupDatabaseGateHostedService(
        StartupDatabaseGate gate,
        StartupDatabaseGateOptions options,
        StartupDatabaseReceipt receipt,
        ServiceId serviceId,
        ILogger<StartupDatabaseGateHostedService> logger)
    {
        this.gate = gate ?? throw new ArgumentNullException(nameof(gate));
        this.options = options ?? throw new ArgumentNullException(nameof(options));
        this.receipt = receipt ?? throw new ArgumentNullException(nameof(receipt));
        this.serviceId = serviceId ?? throw new ArgumentNullException(nameof(serviceId));
        this.logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    /// <summary>
    /// Gets the gate result once it ran, or null before the gate ran. A startup cancelled at the
    /// gate's own checkpoint leaves no result behind.
    /// </summary>
    public StartupDatabaseGateResult? Result { get; private set; }

    /// <inheritdoc />
    /// <remarks>
    /// The result is stored only after the gate published a finite outcome; a failure fails the
    /// host startup with the safe error code only.
    /// </remarks>
    public async Task StartingAsync(CancellationToken cancellationToken)
    {
        var result = await gate.RunAsync(options, receipt, serviceId, cancellationToken)
            .ConfigureAwait(false);
        Result = result;
        if (!result.Succeeded)
        {
            logger.LogError(
                "The startup database gate failed with error code {ErrorCode}.",
                result.ErrorCode);
            throw new InvalidOperationException(
                $"The startup database gate did not complete: {result.ErrorCode}.");
        }

        logger.LogInformation(
            "The startup database gate completed. Migration executor was called: {ExecutorWasCalled}.",
            result.ExecutorWasCalled);
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
