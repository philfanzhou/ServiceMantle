using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using MySqlConnector;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.MariaDb;

/// <summary>Explicit MariaDb deployment support using read-only server database-name evidence.</summary>
/// <remarks>Single-instance identity opens one owned, unpooled, non-enlisted connection.
/// It neither creates the target nor resolves DNS or server aliases.</remarks>
public sealed class MariaDbDatabaseDeploymentCapabilityProvider : IDatabaseDeploymentCapabilityProvider
{
    private readonly IMariaDbCanonicalTargetProbe probe;
    /// <summary>Initializes the provider with the read-only metadata probe.</summary>
    public MariaDbDatabaseDeploymentCapabilityProvider() : this(new MariaDbCanonicalTargetProbe()) { }
    internal MariaDbDatabaseDeploymentCapabilityProvider(IMariaDbCanonicalTargetProbe probe)
    { ArgumentNullException.ThrowIfNull(probe); this.probe = probe; }
    /// <summary>Gets the independent deployment declaration; multi-instance still requires a real lock.</summary>
    public DatabaseDeploymentCapability Capability { get; } =
        new(WellKnownDatabaseProviderIds.MariaDb, DatabaseDeploymentSupport.SingleAndMultiInstance);

    /// <summary>Returns a credential-free TCP/database identity, or empty for unsupported input.</summary>
    /// <exception cref="InvalidOperationException">Read-only identity resolution failed. Driver diagnostics are not exposed.</exception>
    public async ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        MySqlConnectionStringBuilder? builder = null;
        if (string.Equals(target.Provider, Capability.ProviderId, StringComparison.OrdinalIgnoreCase))
            try { builder = new MySqlConnectionStringBuilder(target.ConnectionString); }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException) { }
        if (builder is null || builder.ConnectionProtocol != MySqlConnectionProtocol.Tcp ||
            builder.Port is 0 or > 65535 ||
            string.IsNullOrWhiteSpace(builder.Server) || builder.Server.Contains(',') ||
            builder.Server.Contains('/') || builder.Server.Contains('\\') ||
            !MariaDbDatabaseTarget.IsValidDatabaseName(builder.Database))
        {
            cancellationToken.ThrowIfCancellationRequested();
            return string.Empty;
        }
        builder.Server = builder.Server.Trim();
        builder.Pooling = false;
        builder.AutoEnlist = false;
        string identity;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            var metadata = await probe.ReadAsync(builder, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            var database = metadata.CaseRule switch { 0 => metadata.Database, 1 or 2 => metadata.LowerDatabase, _ => null };
            identity = string.IsNullOrWhiteSpace(database) ? string.Empty : EncodeIdentity(builder.Server.Trim().ToLowerInvariant(), builder.Port, database);
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

    private static string EncodeIdentity(string host, uint port, string database)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        Span<byte> length = stackalloc byte[4];
        foreach (var field in new[] { "ServiceMantle.SingleInstance.MariaDb.v1", host, port.ToString(CultureInfo.InvariantCulture), database })
        {
            var bytes = Encoding.UTF8.GetBytes(field);
            BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
            hash.AppendData(length); hash.AppendData(bytes);
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }
}

internal sealed record MariaDbTargetMetadata(int CaseRule, string? Database, string? LowerDatabase);
internal interface IMariaDbCanonicalTargetProbe
{
    ValueTask<MariaDbTargetMetadata> ReadAsync(MySqlConnectionStringBuilder builder, CancellationToken token);
}
internal sealed class MariaDbCanonicalTargetProbe : IMariaDbCanonicalTargetProbe
{
    internal const string Query = "SELECT @@lower_case_table_names, DATABASE(), LOWER(DATABASE())";
    public async ValueTask<MariaDbTargetMetadata> ReadAsync(MySqlConnectionStringBuilder builder, CancellationToken token)
    {
        MariaDbTargetMetadata result;
        await using (var connection = new MySqlConnection(builder.ConnectionString))
        {
            await connection.OpenAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            await using var command = connection.CreateCommand();
            command.CommandText = Query;
            await using var reader = await command.ExecuteReaderAsync(token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();
            if (!await reader.ReadAsync(token).ConfigureAwait(false))
                result = new(-1, null, null);
            else
                result = new(reader.GetInt32(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2));
            token.ThrowIfCancellationRequested();
        }
        token.ThrowIfCancellationRequested();
        return result;
    }
}
