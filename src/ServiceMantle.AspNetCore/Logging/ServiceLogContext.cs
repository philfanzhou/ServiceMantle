using System.Collections;
using System.Reflection;
using Microsoft.Extensions.Logging;

namespace ServiceMantle.Logging;

/// <summary>
/// Creates structured logging scopes containing stable service and instance identity.
/// </summary>
/// <remarks>
/// Instances are immutable and thread-safe. Extension values are not sanitized; callers must only
/// pass values that are safe for their configured logging providers.
/// </remarks>
public sealed class ServiceLogContext
{
    private const int MaximumExtensionFieldCount = 32;
    private const int MaximumFieldNameLength = 128;
    private const int MaximumServiceVersionLength = 128;
    private const string UnknownServiceVersion = "unknown";

    private static readonly HashSet<string> ProtectedFieldNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ServiceLogFieldNames.ServiceName,
        ServiceLogFieldNames.ServiceVersion,
        ServiceLogFieldNames.InstanceId,
        ServiceLogFieldNames.CorrelationId,
    };

    private readonly KeyValuePair<string, object?>[] identityFields;

    /// <summary>
    /// Initializes a structured logging context.
    /// </summary>
    /// <param name="serviceId">The stable service identity used as <c>ServiceName</c>.</param>
    /// <param name="instanceId">The identity of this running instance.</param>
    /// <param name="serviceVersion">The non-empty running service version.</param>
    public ServiceLogContext(
        ServiceId serviceId,
        InstanceId instanceId,
        string serviceVersion)
    {
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(instanceId);
        ArgumentNullException.ThrowIfNull(serviceVersion);

        var normalizedVersion = serviceVersion.Trim();
        if (!IsValidServiceVersion(normalizedVersion))
        {
            throw new ArgumentException(
                $"The service version must contain between 1 and {MaximumServiceVersionLength} printable characters.",
                nameof(serviceVersion));
        }

        ServiceName = serviceId.Value;
        ServiceVersion = normalizedVersion;
        InstanceId = instanceId.Value;
        identityFields =
        [
            new(ServiceLogFieldNames.ServiceName, ServiceName),
            new(ServiceLogFieldNames.ServiceVersion, ServiceVersion),
            new(ServiceLogFieldNames.InstanceId, InstanceId),
        ];
    }

    /// <summary>
    /// Gets the stable service name.
    /// </summary>
    public string ServiceName { get; }

    /// <summary>
    /// Gets the running service version.
    /// </summary>
    public string ServiceVersion { get; }

    /// <summary>
    /// Gets the running instance identity.
    /// </summary>
    public string InstanceId { get; }

    /// <summary>
    /// Begins an <see cref="ILogger"/> scope containing the protected identity fields and optional
    /// controlled extension fields.
    /// </summary>
    /// <param name="logger">The logger whose scope should be enriched.</param>
    /// <param name="extensionFields">Optional per-operation structured fields.</param>
    /// <returns>A handle that deterministically ends the logging scope when disposed.</returns>
    /// <exception cref="ArgumentException">
    /// An extension field is invalid, missing a value, duplicated, or attempts to override an
    /// identity field.
    /// </exception>
    public IDisposable BeginScope(
        ILogger logger,
        IEnumerable<KeyValuePair<string, object?>>? extensionFields = null)
    {
        ArgumentNullException.ThrowIfNull(logger);

        var fields = new List<KeyValuePair<string, object?>>(identityFields);
        if (extensionFields is not null)
        {
            var extensionNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var field in extensionFields)
            {
                if (fields.Count - identityFields.Length >= MaximumExtensionFieldCount)
                {
                    throw new ArgumentException(
                        $"A logging scope supports at most {MaximumExtensionFieldCount} extension fields.",
                        nameof(extensionFields));
                }

                if (!IsValidFieldName(field.Key))
                {
                    throw new ArgumentException(
                        "A logging extension field name is invalid.",
                        nameof(extensionFields));
                }

                if (field.Value is null)
                {
                    throw new ArgumentException(
                        "A logging extension field value is missing.",
                        nameof(extensionFields));
                }

                if (ProtectedFieldNames.Contains(field.Key))
                {
                    throw new ArgumentException(
                        "A protected logging identity field cannot be overridden.",
                        nameof(extensionFields));
                }

                if (!extensionNames.Add(field.Key))
                {
                    throw new ArgumentException(
                        "A logging extension field is duplicated.",
                        nameof(extensionFields));
                }

                fields.Add(field);
            }
        }

        return logger.BeginScope(new StructuredScopeState(fields.ToArray())) ?? NullScope.Instance;
    }

    /// <summary>
    /// Begins the trusted per-request scope containing the identity fields and the already validated
    /// Correlation ID.
    /// </summary>
    /// <remarks>
    /// This entry point is assembly-internal and exists only for the ServiceMantle Correlation ID
    /// middleware. It is not a general field-override bypass: it accepts nothing but a Correlation ID
    /// that the middleware has already resolved, and it neither parses nor normalizes that value.
    /// </remarks>
    internal IDisposable BeginRequestScope(ILogger logger, string correlationId)
    {
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentException.ThrowIfNullOrEmpty(correlationId);

        var fields = new KeyValuePair<string, object?>[identityFields.Length + 1];
        identityFields.CopyTo(fields, 0);
        fields[^1] = new(ServiceLogFieldNames.CorrelationId, correlationId);

        return logger.BeginScope(new StructuredScopeState(fields)) ?? NullScope.Instance;
    }

    internal static string ResolveServiceVersion(string? configuredVersion)
    {
        if (configuredVersion is not null)
        {
            var normalizedVersion = configuredVersion.Trim();
            if (!IsValidServiceVersion(normalizedVersion))
            {
                throw new ArgumentException(
                    $"The service version must contain between 1 and {MaximumServiceVersionLength} printable characters.",
                    nameof(configuredVersion));
            }

            return normalizedVersion;
        }

        var entryAssembly = Assembly.GetEntryAssembly();
        var informationalVersion = entryAssembly?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
            .InformationalVersion?
            .Trim();
        if (IsValidServiceVersion(informationalVersion))
        {
            return informationalVersion!;
        }

        var assemblyVersion = entryAssembly?.GetName().Version?.ToString();
        return IsValidServiceVersion(assemblyVersion)
            ? assemblyVersion!
            : UnknownServiceVersion;
    }

    private static bool IsValidFieldName(string? fieldName)
    {
        if (fieldName is null ||
            fieldName.Length is < 1 or > MaximumFieldNameLength ||
            !IsAsciiLetter(fieldName[0]))
        {
            return false;
        }

        for (var index = 1; index < fieldName.Length; index++)
        {
            var character = fieldName[index];
            if (!IsAsciiLetter(character) &&
                character is not (>= '0' and <= '9') &&
                character is not ('_' or '.' or '-'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidServiceVersion(string? serviceVersion)
    {
        if (serviceVersion is null ||
            serviceVersion.Length is < 1 or > MaximumServiceVersionLength)
        {
            return false;
        }

        foreach (var character in serviceVersion)
        {
            if (char.IsControl(character))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsAsciiLetter(char character) =>
        character is >= 'A' and <= 'Z' or >= 'a' and <= 'z';

    private sealed class StructuredScopeState : IReadOnlyList<KeyValuePair<string, object?>>
    {
        private readonly KeyValuePair<string, object?>[] fields;

        internal StructuredScopeState(KeyValuePair<string, object?>[] fields)
        {
            this.fields = fields;
        }

        public int Count => fields.Length;

        public KeyValuePair<string, object?> this[int index] => fields[index];

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator() =>
            ((IEnumerable<KeyValuePair<string, object?>>)fields).GetEnumerator();

        IEnumerator IEnumerable.GetEnumerator() => fields.GetEnumerator();

        public override string ToString() => "ServiceMantle structured logging context";
    }

    private sealed class NullScope : IDisposable
    {
        internal static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
