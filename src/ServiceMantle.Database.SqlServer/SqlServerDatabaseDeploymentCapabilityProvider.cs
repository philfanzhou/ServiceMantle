using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Net;
using Microsoft.Data.SqlClient;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.SqlServer;

/// <summary>Explicit SQL Server deployment support using read-only server database-name evidence.</summary>
/// <remarks>Single-instance identity opens one owned, unpooled, non-enlisted connection.
/// It neither creates the target nor resolves DNS or server aliases.</remarks>
public sealed class SqlServerDatabaseDeploymentCapabilityProvider : IDatabaseDeploymentCapabilityProvider
{
    private readonly ISqlServerCanonicalTargetProbe probe;
    /// <summary>Initializes the provider with the read-only metadata probe.</summary>
    public SqlServerDatabaseDeploymentCapabilityProvider() : this(new SqlServerCanonicalTargetProbe()) { }
    internal SqlServerDatabaseDeploymentCapabilityProvider(ISqlServerCanonicalTargetProbe probe)
    { ArgumentNullException.ThrowIfNull(probe); this.probe = probe; }
    /// <summary>Gets the independent deployment declaration; multi-instance still requires a real lock.</summary>
    public DatabaseDeploymentCapability Capability { get; } =
        new(WellKnownDatabaseProviderIds.SqlServer, DatabaseDeploymentSupport.SingleAndMultiInstance);

    /// <summary>Returns a credential-free TCP/database identity, or empty for unsupported input.</summary>
    /// <exception cref="InvalidOperationException">Read-only identity resolution failed. Driver diagnostics are not exposed.</exception>
    public async ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        SqlConnectionStringBuilder? builder = null;
        if (string.Equals(target.Provider, Capability.ProviderId, StringComparison.OrdinalIgnoreCase))
            try { builder = new SqlConnectionStringBuilder(target.ConnectionString); }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException) { }
        if (builder is null || !TryParseEndpoint(builder.DataSource, out var host, out var port) ||
            string.IsNullOrWhiteSpace(builder.InitialCatalog) || !string.IsNullOrEmpty(builder.AttachDBFilename) ||
            builder.UserInstance || !string.IsNullOrEmpty(builder.FailoverPartner))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return string.Empty;
        }
        builder.DataSource = $"tcp:{(host.Contains(':') ? "[" + host + "]" : host)},{port}";
        builder.Pooling = false;
        builder.Enlist = false;
        string identity;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await probe.ReadAsync(builder, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            identity = string.IsNullOrWhiteSpace(metadata) ? string.Empty : EncodeIdentity(host, port, metadata);
        }
        catch
        {
            cancellationToken.ThrowIfCancellationRequested();
            throw new InvalidOperationException("The database target identity could not be resolved.");
        }
        // Includes normal and exceptional resource release performed by the probe.
        cancellationToken.ThrowIfCancellationRequested();
        return identity;
    }

    private static bool TryParseEndpoint(string source, out string host, out int port)
    {
        host = string.Empty; port = 1433;
        var endpoint = source.Trim();
        if (endpoint.StartsWith("tcp:", StringComparison.OrdinalIgnoreCase)) endpoint = endpoint[4..];
        var parts = endpoint.Split(',');
        if (parts.Length is < 1 or > 2) return false;
        host = parts[0].Trim().ToLowerInvariant();
        if (host.StartsWith('[') && host.EndsWith(']'))
        {
            if (!IPAddress.TryParse(host[1..^1], out var address) || address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6) return false;
            host = address.ToString();
        }
        else if (Uri.CheckHostName(host) == UriHostNameType.Unknown || host.Contains(':')) return false;
        if (parts.Length == 2 && (!int.TryParse(parts[1].Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out port) || port is < 1 or > 65535)) return false;
        return true;
    }

    private static string EncodeIdentity(string host, int port, string database)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in new[] { "ServiceMantle.SingleInstance.SqlServer.v1", host, port.ToString(CultureInfo.InvariantCulture), database })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

internal interface ISqlServerCanonicalTargetProbe
{
    ValueTask<string?> ReadAsync(SqlConnectionStringBuilder builder, CancellationToken token);
}
internal sealed class SqlServerCanonicalTargetProbe : ISqlServerCanonicalTargetProbe
{
    internal const string Query = "SELECT DB_NAME()";
    public async ValueTask<string?> ReadAsync(SqlConnectionStringBuilder builder, CancellationToken token)
    {
        string? result;
        await using (var connection = new SqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText = Query;
            result = await command.ExecuteScalarAsync(token).ConfigureAwait(false) as string;
            token.ThrowIfCancellationRequested();
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
