using System.Text.Json;
using ServiceMantle.Audit;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

internal static class ManagementAuditEntityMapper
{
    private static readonly JsonSerializerOptions MetadataJsonOptions = new()
    {
        WriteIndented = false
    };

    internal static ManagementAuditLogEntity ConvertToEntity(Guid id, ManagementAuditEvent auditEvent)
    {
        return new ManagementAuditLogEntity
        {
            Id = id,
            OperatorId = auditEvent.Operator.OperatorId,
            OperatorDisplayName = auditEvent.Operator.DisplayName,
            OperatorSource = auditEvent.Operator.Source.Value,
            Action = auditEvent.Action.Value,
            TargetType = auditEvent.Target.Type.Value,
            TargetId = auditEvent.Target.Id,
            Outcome = auditEvent.Outcome,
            OccurredAtUtc = auditEvent.OccurredAtUtc.UtcDateTime,
            ClientIp = auditEvent.ClientIp,
            CorrelationId = auditEvent.CorrelationId,
            SecurityDescription = auditEvent.SecurityDescription,
            MetadataJson = auditEvent.Metadata.Count == 0
                ? null
                : JsonSerializer.Serialize(auditEvent.Metadata, MetadataJsonOptions)
        };
    }

    internal static ManagementAuditRecord ConvertToRecord(ManagementAuditLogEntity entity)
    {
        if (!ManagementAuditOperatorSource.TryParse(entity.OperatorSource, out var source) || source is null)
        {
            throw new ManagementAuditException(
                "audit.entity_invalid",
                "The stored audit operator source is invalid.");
        }

        if (!ManagementAuditAction.TryParse(entity.Action, out var action) || action is null)
        {
            throw new ManagementAuditException(
                "audit.entity_invalid",
                "The stored audit action is invalid.");
        }

        if (!ManagementAuditTargetType.TryParse(entity.TargetType, out var targetType) || targetType is null)
        {
            throw new ManagementAuditException(
                "audit.entity_invalid",
                "The stored audit target type is invalid.");
        }

        if (!Enum.IsDefined(entity.Outcome))
        {
            throw new ManagementAuditException(
                "audit.entity_invalid",
                "The stored audit outcome value is invalid.");
        }

        var operatorInfo = ManagementAuditOperator.Create(source, entity.OperatorId, entity.OperatorDisplayName);
        var target = ManagementAuditTarget.Create(targetType, entity.TargetId);
        var metadata = string.IsNullOrEmpty(entity.MetadataJson)
            ? new Dictionary<string, string>(0)
            : JsonSerializer.Deserialize<Dictionary<string, string>>(entity.MetadataJson, MetadataJsonOptions)
              ?? new Dictionary<string, string>(0);

        return new ManagementAuditRecord(
            entity.Id,
            operatorInfo,
            action,
            target,
            entity.Outcome,
            new DateTimeOffset(DateTime.SpecifyKind(entity.OccurredAtUtc, DateTimeKind.Utc)),
            entity.ClientIp,
            entity.CorrelationId,
            entity.SecurityDescription,
            metadata);
    }
}
