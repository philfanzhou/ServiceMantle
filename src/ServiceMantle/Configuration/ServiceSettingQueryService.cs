using System.Globalization;
using System.Text;
using System.Text.Json;

namespace ServiceMantle.Configuration;

/// <summary>
/// Provides authorization- and transport-independent safe queries over registered service settings.
/// </summary>
public sealed class ServiceSettingQueryService
{
    private readonly ServiceSettingSnapshotLoader loader;
    private readonly IReadOnlyList<ServiceSettingDefinitionProjection> definitions;

    /// <summary>Initializes a setting query service.</summary>
    public ServiceSettingQueryService(
        ServiceSettingDefinitionRegistry registry,
        ServiceSettingSnapshotLoader loader)
    {
        ArgumentNullException.ThrowIfNull(registry);
        ArgumentNullException.ThrowIfNull(loader);

        this.loader = loader;
        definitions = registry.Definitions
            .Select(definition => new ServiceSettingDefinitionProjection(definition))
            .ToList()
            .AsReadOnly();
    }

    /// <summary>Gets safe definition metadata in stable normalized-key order.</summary>
    public IReadOnlyList<ServiceSettingDefinitionProjection> GetDefinitions() => definitions;

    /// <summary>
    /// Refreshes once and projects the complete successful snapshot without exposing sensitive values.
    /// </summary>
    /// <exception cref="OperationCanceledException">The caller cancelled the query.</exception>
    public async ValueTask<ServiceSettingCurrentQueryResult> GetCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        var refresh = await loader.RefreshAsync(cancellationToken).ConfigureAwait(false);
        if (!refresh.Succeeded)
        {
            return ServiceSettingCurrentQueryResult.Failure(refresh.Errors);
        }

        var snapshot = refresh.Snapshot!;
        var values = new List<ServiceSettingCurrentValueProjection>(snapshot.Values.Count);
        foreach (var value in snapshot.Values.Values.OrderBy(item => item.Key, StringComparer.Ordinal))
        {
            var definition = new ServiceSettingDefinitionProjection(value.Definition);
            var source = !value.HasValue
                ? ServiceSettingValueSource.Missing
                : value.IsDefault
                    ? ServiceSettingValueSource.Default
                    : ServiceSettingValueSource.Persisted;
            values.Add(new ServiceSettingCurrentValueProjection(
                definition,
                value.HasValue,
                source,
                definition.IsSensitive || !value.HasValue ? null : NormalizeValue(value)));
        }

        return ServiceSettingCurrentQueryResult.Success(
            snapshot.Version,
            values.AsReadOnly());
    }

    private static string NormalizeValue(ServiceSettingValue value) => value.ValueType switch
    {
        ServiceSettingValueType.String => value.GetString(),
        ServiceSettingValueType.Number => value.GetNumber().ToString("G29", CultureInfo.InvariantCulture),
        ServiceSettingValueType.Boolean => value.GetBoolean() ? "true" : "false",
        ServiceSettingValueType.Json => NormalizeJson(value.GetJson()),
        _ => throw new InvalidOperationException(),
    };

    private static string NormalizeJson(JsonElement value)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            value.WriteTo(writer);
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
