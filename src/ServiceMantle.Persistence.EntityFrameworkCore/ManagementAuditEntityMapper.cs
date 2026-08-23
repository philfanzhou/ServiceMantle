using System.Text.Encodings.Web;
using System.Text.Json;
using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

internal static class ManagementAuditEntityMapper
{
    internal const int MaxMetadataJsonLength = 256 * 1024;

    private static readonly JsonDocumentOptions MetadataDocumentOptions = new()
    {
        AllowTrailingCommas = false,
        CommentHandling = JsonCommentHandling.Disallow,
        MaxDepth = 8
    };

    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        WriteIndented = false
    };

    internal static ManagementAuditLogEntity ConvertToEntity(Guid id, ManagementAuditEvent auditEvent)
    {
        ArgumentNullException.ThrowIfNull(auditEvent);
        var safeEvent = Revalidate(auditEvent);
        var metadataJson = safeEvent.Metadata.Count == 0
            ? null
            : JsonSerializer.Serialize(safeEvent.Metadata, MetadataJsonOptions);
        if (metadataJson?.Length > MaxMetadataJsonLength)
        {
            throw new ManagementAuditException(
                "audit.metadata_invalid",
                "The serialized audit metadata exceeds the persistence limit.");
        }

        return new ManagementAuditLogEntity
        {
            Id = id,
            OperatorId = safeEvent.Operator.OperatorId,
            OperatorDisplayName = safeEvent.Operator.DisplayName,
            OperatorSource = safeEvent.Operator.Source.Value,
            Action = safeEvent.Action.Value,
            TargetType = safeEvent.Target.Type.Value,
            TargetId = safeEvent.Target.Id,
            Outcome = safeEvent.Outcome,
            OccurredAtUtc = safeEvent.OccurredAtUtc.UtcDateTime,
            ClientIp = safeEvent.ClientIp,
            CorrelationId = safeEvent.CorrelationId,
            SecurityDescription = safeEvent.SecurityDescription,
            MetadataJson = metadataJson
        };
    }

    internal static ManagementAuditRecord ConvertToRecord(ManagementAuditLogEntity entity)
    {
        ArgumentNullException.ThrowIfNull(entity);

        if (!ManagementAuditOperatorSource.TryParse(entity.OperatorSource, out var source) || source is null
            || !ManagementAuditAction.TryParse(entity.Action, out var action) || action is null
            || !ManagementAuditTargetType.TryParse(entity.TargetType, out var targetType) || targetType is null
            || !Enum.IsDefined(entity.Outcome))
        {
            throw InvalidStoredEntity();
        }

        Dictionary<string, string> metadata;
        try
        {
            metadata = ParseMetadata(entity.MetadataJson);
        }
        catch (ManagementAuditException)
        {
            throw;
        }
        catch (Exception exception) when (exception is JsonException or InvalidOperationException or ArgumentException)
        {
            throw InvalidStoredEntity(exception);
        }

        try
        {
            var operatorInfo = ManagementAuditOperator.Create(
                source,
                entity.OperatorId,
                entity.OperatorDisplayName);
            var target = ManagementAuditTarget.Create(targetType, entity.TargetId);
            var safeEvent = ManagementAuditEvent.Create(
                operatorInfo,
                action,
                target,
                entity.Outcome,
                new DateTimeOffset(DateTime.SpecifyKind(entity.OccurredAtUtc, DateTimeKind.Utc)),
                entity.ClientIp,
                entity.CorrelationId,
                entity.SecurityDescription,
                metadata);

            return new ManagementAuditRecord(
                entity.Id,
                safeEvent.Operator,
                safeEvent.Action,
                safeEvent.Target,
                safeEvent.Outcome,
                safeEvent.OccurredAtUtc,
                safeEvent.ClientIp,
                safeEvent.CorrelationId,
                safeEvent.SecurityDescription,
                safeEvent.Metadata);
        }
        catch (ManagementAuditException exception)
        {
            throw InvalidStoredEntity(exception);
        }
    }

    private static ManagementAuditException InvalidStoredEntity(Exception? innerException = null) =>
        new(
            "audit.entity_invalid",
            "The stored audit entity failed validation.",
            innerException);

    private static ManagementAuditEvent Revalidate(ManagementAuditEvent auditEvent) =>
        ManagementAuditEvent.Create(
            auditEvent.Operator,
            auditEvent.Action,
            auditEvent.Target,
            auditEvent.Outcome,
            auditEvent.OccurredAtUtc,
            auditEvent.ClientIp,
            auditEvent.CorrelationId,
            auditEvent.SecurityDescription,
            new Dictionary<string, string>(auditEvent.Metadata, StringComparer.Ordinal));

    private static Dictionary<string, string> ParseMetadata(string? metadataJson)
    {
        if (string.IsNullOrEmpty(metadataJson))
        {
            return new Dictionary<string, string>(0, StringComparer.Ordinal);
        }

        if (metadataJson.Length > MaxMetadataJsonLength)
        {
            throw InvalidStoredEntity();
        }

        using var document = JsonDocument.Parse(metadataJson, MetadataDocumentOptions);
        if (document.RootElement.ValueKind != JsonValueKind.Object)
        {
            throw InvalidStoredEntity();
        }

        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var property in document.RootElement.EnumerateObject())
        {
            if (metadata.Count >= ManagementAuditEvent.MaxMetadataEntries
                || property.Value.ValueKind != JsonValueKind.String
                || !metadata.TryAdd(property.Name, property.Value.GetString() ?? string.Empty))
            {
                throw InvalidStoredEntity();
            }
        }

        return metadata;
    }
}
