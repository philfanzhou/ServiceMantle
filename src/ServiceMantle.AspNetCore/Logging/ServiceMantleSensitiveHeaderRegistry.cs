using System.Collections.Frozen;
using ServiceMantle.Logging;

namespace ServiceMantle.AspNetCore;

internal sealed record ServiceMantleSensitiveHeaderRegistration(
    ServiceMantleSensitiveHeadersOptions Options,
    bool ConfigureFailed);

/// <summary>Exposes the immutable startup snapshot of denied request Header names.</summary>
public sealed class ServiceMantleSensitiveHeaderRegistry
{
    private readonly IEnumerable<ServiceMantleSensitiveHeaderRegistration> registrations;
    private readonly object sync = new();
    private volatile FrozenSet<string>? snapshot;

    internal ServiceMantleSensitiveHeaderRegistry(
        IEnumerable<ServiceMantleSensitiveHeaderRegistration> registrations)
    {
        this.registrations = registrations;
    }

    /// <summary>
    /// Gets the case-insensitive immutable set containing built-in and consumer-added names.
    /// </summary>
    public IReadOnlySet<string> DeniedHeaderNames => GetRequiredSnapshot();

    /// <summary>Determines whether a valid HTTP token Header name belongs to the denied snapshot.</summary>
    public bool IsSensitive(string headerName)
    {
        if (!TryNormalizeHeaderName(headerName, out var normalizedName))
        {
            throw new ArgumentException("A request Header name must be a valid HTTP token.", nameof(headerName));
        }

        return GetRequiredSnapshot().Contains(normalizedName);
    }

    internal FrozenSet<string> GetRequiredSnapshot()
    {
        if (snapshot is not null)
        {
            return snapshot;
        }

        lock (sync)
        {
            if (snapshot is not null)
            {
                return snapshot;
            }

            ServiceMantleSensitiveHeaderRegistration[] materialized;
            try
            {
                materialized = registrations.ToArray();
            }
            catch
            {
                throw Failure(
                    WellKnownSensitiveHeaderConfigurationErrorCodes.EnumerationFailed,
                    "Registrations");
            }

            var names = new HashSet<string>(
                StructuredLogSanitizerDefaults.BuiltInDeniedHeaderNames,
                StringComparer.OrdinalIgnoreCase);
            foreach (var registration in materialized)
            {
                if (registration.ConfigureFailed)
                {
                    throw Failure(
                        WellKnownSensitiveHeaderConfigurationErrorCodes.ConfigureFailed,
                        "Configure");
                }

                string[] configuredNames;
                try
                {
                    configuredNames = registration.Options.DeniedHeaderNames.ToArray();
                }
                catch
                {
                    throw Failure(
                        WellKnownSensitiveHeaderConfigurationErrorCodes.EnumerationFailed,
                        nameof(ServiceMantleSensitiveHeadersOptions.DeniedHeaderNames));
                }

                foreach (var name in configuredNames)
                {
                    if (!TryNormalizeHeaderName(name, out var normalizedName))
                    {
                        throw Failure(
                            WellKnownSensitiveHeaderConfigurationErrorCodes.InvalidName,
                            nameof(ServiceMantleSensitiveHeadersOptions.DeniedHeaderNames));
                    }

                    names.Add(normalizedName);
                }
            }

            snapshot = names.ToFrozenSet(StringComparer.OrdinalIgnoreCase);
            return snapshot;
        }
    }

    private static bool TryNormalizeHeaderName(string? name, out string normalizedName)
    {
        normalizedName = string.Empty;
        if (string.IsNullOrEmpty(name))
        {
            return false;
        }

        foreach (var character in name)
        {
            if (!IsHttpTokenCharacter(character))
            {
                return false;
            }
        }

        normalizedName = name.ToLowerInvariant();
        return true;
    }

    private static bool IsHttpTokenCharacter(char character) =>
        character is >= 'A' and <= 'Z' or
            >= 'a' and <= 'z' or
            >= '0' and <= '9' or
            '!' or '#' or '$' or '%' or '&' or '\'' or '*' or '+' or '-' or '.' or '^' or '_' or
            '`' or '|' or '~';

    private static ServiceMantleSensitiveHeaderConfigurationException Failure(
        string errorCode,
        string fieldName) => new(errorCode, fieldName);
}
