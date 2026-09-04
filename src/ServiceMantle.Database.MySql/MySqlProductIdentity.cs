using System.Data.Common;
using MySqlConnector;

namespace ServiceMantle.Database.MySql;

internal static class MySqlProductIdentity
{
    internal const string Query = "SELECT VERSION(), @@version, @@version_comment";

    internal static bool IsSupported(string? handshake, string? version, string? systemVersion, string? comment)
    {
        if (!IsSignal(handshake, 64) || !IsSignal(version, 64) ||
            !IsSignal(systemVersion, 64) || !IsSignal(comment, 128) ||
            !string.Equals(handshake, version, StringComparison.Ordinal) ||
            !string.Equals(version, systemVersion, StringComparison.Ordinal) ||
            !string.Equals(comment, "MySQL Community Server - GPL", StringComparison.Ordinal))
        {
            return false;
        }

        var parts = version!.Split('.');
        return parts.Length == 3 && parts[0] is "8" or "9" &&
            parts.All(part => part.Length is >= 1 and <= 3 &&
                (part.Length == 1 || part[0] != '0') && part.All(char.IsAsciiDigit));
    }

    internal static async ValueTask<MySqlProbeOutcome> ProbeAsync(
        DbConnection connection, int commandTimeoutSeconds, CancellationToken cancellationToken)
    {
        try
        {
            var handshake = connection.ServerVersion;
            await using var command = connection.CreateCommand();
            command.CommandText = Query;
            command.CommandTimeout = commandTimeoutSeconds;
            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (reader.FieldCount != 3 || !await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return MySqlProbeOutcome.ServerProductMismatch;
            }

            var supported = IsSupported(handshake, reader.GetValue(0) as string,
                reader.GetValue(1) as string, reader.GetValue(2) as string);
            var extraRow = await reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return supported && !extraRow
                ? MySqlProbeOutcome.Success
                : MySqlProbeOutcome.ServerProductMismatch;
        }
        catch (Exception exception)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (MySqlProbeFailureClassifier.Classify(exception) == MySqlProbeOutcome.ConnectionFailed)
            {
                return MySqlProbeOutcome.ConnectionFailed;
            }

            // SQL rejection is unavailable product evidence; an unknown internal failure retains
            // the existing generic failure classification. Neither exposes raw server messages.
            return exception is MySqlException
                ? MySqlProbeOutcome.ServerProductMismatch
                : MySqlProbeOutcome.ValidationFailed;
        }
    }

    private static bool IsSignal(string? value, int maximumLength) =>
        value is { Length: > 0 } && value.Length <= maximumLength && !value.Any(char.IsControl);
}

internal static class MySqlProbeConnection
{
    internal static DbConnection Create(MySqlConnectionStringBuilder settings) =>
        new MySqlConnection(settings.ConnectionString);

    internal static void AddParameter(DbCommand command, string name, string value)
    {
        var parameter = command.CreateParameter();
        parameter.ParameterName = name;
        parameter.Value = value;
        command.Parameters.Add(parameter);
    }

    internal static async ValueTask DisposeSafelyAsync(DbConnection? connection)
    {
        if (connection is null)
        {
            return;
        }

        try
        {
            await connection.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // Cleanup cannot replace the classified primary outcome.
        }
    }
}
