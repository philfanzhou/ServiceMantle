using System.Buffers.Binary;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Npgsql;
using ServiceMantle.Bootstrap;

namespace ServiceMantle.Database.PostgreSql;

/// <summary>Explicit PostgreSQL deployment support with a credential-free TCP target identity.</summary>
/// <remarks>No connection is opened. DNS aliases and database-name case aliases are not resolved.</remarks>
public sealed class PostgreSqlDatabaseDeploymentCapabilityProvider : IDatabaseDeploymentCapabilityProvider
{
    /// <summary>Gets the independent declaration; a multi-instance deployment still needs a real lock.</summary>
    public DatabaseDeploymentCapability Capability { get; } =
        new(WellKnownDatabaseProviderIds.PostgreSql, DatabaseDeploymentSupport.SingleAndMultiInstance);

    /// <summary>Returns a stable identity for an explicit single TCP host and database, or empty for unsupported input.</summary>
    public ValueTask<string> GetCanonicalTargetIdentityAsync(BootstrapDatabaseConfiguration target,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(target);
        cancellationToken.ThrowIfCancellationRequested();
        var identity = string.Empty;
        if (string.Equals(target.Provider, Capability.ProviderId, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var builder = new NpgsqlConnectionStringBuilder(target.ConnectionString);
                var host = builder.Host?.Trim();
                if (!string.IsNullOrWhiteSpace(host) && !host.Contains(',') &&
                    !host.Contains('/') && !host.Contains('\\') &&
                    !string.IsNullOrWhiteSpace(builder.Database))
                {
                    using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                    Span<byte> length = stackalloc byte[4];
                    foreach (var field in new[] { "ServiceMantle.SingleInstance.PostgreSql.v1",
                                 host.ToLowerInvariant(), builder.Port.ToString(CultureInfo.InvariantCulture), builder.Database })
                    {
                        var bytes = Encoding.UTF8.GetBytes(field);
                        BinaryPrimitives.WriteInt32BigEndian(length, bytes.Length);
                        hash.AppendData(length);
                        hash.AppendData(bytes);
                    }
                    identity = Convert.ToHexString(hash.GetHashAndReset());
                }
            }
            catch (Exception exception) when (exception is ArgumentException or FormatException or OverflowException)
            {
                // The parser's message and exception chain can contain connection values.
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(identity);
    }
}
