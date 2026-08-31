using System.Globalization;
using Oracle.ManagedDataAccess.Client;

namespace ServiceMantle.Database.Oracle;

internal static class OracleDatabaseTarget
{
    internal const int MinimumSupportedServerMajorVersion = 19;
    internal const int MaximumUserNameLength = 128;
    internal const int MaximumPasswordLength = 30;
    internal const int MaximumConnectionTimeoutSeconds = 8;
    internal const int CommandTimeoutSeconds = 5;
    internal static readonly TimeSpan CompensationTimeout = TimeSpan.FromSeconds(5);

    internal static bool TryBuildConnectionString(
        string connectionString,
        out OracleConnectionStringBuilder builder)
    {
        try
        {
            builder = new OracleConnectionStringBuilder(connectionString);
            return true;
        }
        catch (ArgumentException)
        {
            builder = null!;
            return false;
        }
    }

    internal static bool TryNormalizeServerVersion(string? value, out int majorVersion)
    {
        majorVersion = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var parts = value.Trim().Split('.');
        if (parts.Length is < 1 or > 5 || parts.Any(part =>
                part.Length == 0 ||
                !int.TryParse(part, NumberStyles.None, CultureInfo.InvariantCulture, out _)))
        {
            return false;
        }

        return int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out majorVersion);
    }

    internal static bool TryGetTargetIdentity(
        OracleConnectionStringBuilder builder,
        out string userName,
        out string password,
        out string dataSource)
    {
        userName = string.Empty;
        password = string.Empty;
        dataSource = string.Empty;

        if (HasUnsupportedAuthentication(builder) ||
            !TryNormalizeUserName(builder.UserID, out userName) ||
            !IsValidPassword(builder.Password))
        {
            return false;
        }

        password = builder.Password;
        dataSource = builder.DataSource?.Trim() ?? string.Empty;
        return dataSource.Length > 0;
    }

    internal static bool TryGetAdministrativeIdentity(
        OracleConnectionStringBuilder builder,
        out string userName,
        out string dataSource)
    {
        userName = string.Empty;
        dataSource = string.Empty;
        if (HasUnsupportedAuthentication(builder) ||
            string.IsNullOrWhiteSpace(builder.UserID) ||
            string.IsNullOrEmpty(builder.Password))
        {
            return false;
        }

        userName = builder.UserID.Trim().ToUpperInvariant();
        dataSource = builder.DataSource?.Trim() ?? string.Empty;
        return dataSource.Length > 0;
    }

    internal static bool TryNormalizeUserName(string? value, out string userName)
    {
        userName = string.Empty;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var candidate = value.Trim();
        if (candidate.Length > MaximumUserNameLength ||
            !char.IsAsciiLetter(candidate[0]) ||
            candidate.Any(character =>
                !(char.IsAsciiLetterOrDigit(character) || character is '_' or '$' or '#')))
        {
            return false;
        }

        candidate = candidate.ToUpperInvariant();
        if (candidate.StartsWith("C##", StringComparison.Ordinal))
        {
            return false;
        }

        userName = candidate;
        return true;
    }

    internal static bool IsValidPassword(string? value) =>
        value is { Length: >= 1 and <= MaximumPasswordLength } &&
        value.All(character => character is >= ' ' and <= '~' && character != '"');

    internal static bool HasSameDataSource(string first, string second) =>
        string.Equals(first.Trim(), second.Trim(), StringComparison.Ordinal);

    internal static void ApplySafeTimeout(OracleConnectionStringBuilder builder)
    {
        if (builder.ConnectionTimeout <= 0 || builder.ConnectionTimeout > MaximumConnectionTimeoutSeconds)
        {
            builder.ConnectionTimeout = MaximumConnectionTimeoutSeconds;
        }
    }

    internal static void IsolateAdministrativeConnection(OracleConnectionStringBuilder builder)
    {
        builder.Pooling = false;
        builder["Enlist"] = "false";
        ApplySafeTimeout(builder);
    }

    internal static string QuoteIdentifier(string userName) => $"\"{userName}\"";

    internal static string QuotePassword(string password) => $"\"{password}\"";

    private static bool HasUnsupportedAuthentication(OracleConnectionStringBuilder builder) =>
        builder.ShouldSerialize("DBA Privilege") ||
        builder.ShouldSerialize("Proxy User Id") ||
        builder.ShouldSerialize("Proxy Password") ||
        builder.ShouldSerialize("Wallet Location") ||
        builder.ShouldSerialize("Token Authentication");
}
