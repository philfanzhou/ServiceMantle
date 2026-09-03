using Oracle.ManagedDataAccess.Client;

namespace ServiceMantle.Database.Oracle.Tests;

// CI-only diagnostics. Do not attach a driver exception: its text can contain connection details.
internal static class OracleListenerPreflight
{
    internal static async Task VerifyAsync(Func<CancellationToken, Task> probe, CancellationToken cancellationToken)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            await probe(cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
        }
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException("Oracle listener preflight was cancelled.", cancellationToken);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                "Oracle listener preflight failed: " + Classify(exception is OracleException oracle ? oracle.Number : null) + ".");
        }
    }

    internal static string Classify(int? number) => number switch
    {
        1017 => "credentials_rejected (ORA-01017); verify initialization and credential configuration",
        28000 => "account_locked (ORA-28000)",
        28001 => "password_expired (ORA-28001)",
        1031 or 1045 => "permission_denied",
        12154 or 12514 or 12541 or 12545 => "listener_or_service_unavailable",
        12170 or 12535 => "connection_timeout",
        _ => "unexpected_probe_failure"
    };
}
