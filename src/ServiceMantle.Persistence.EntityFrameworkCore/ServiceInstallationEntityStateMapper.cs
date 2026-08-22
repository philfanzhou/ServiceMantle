using ServiceMantle.Installation;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

internal static class ServiceInstallationEntityStateMapper
{
    internal static ServiceInstallationState ConvertToState(ServiceInstallationEntity entity)
    {
        Validate(entity);

        var serviceId = ServiceId.Parse(entity.ServiceId);
        return entity.Status == InstallationStatus.PendingSetup
            ? ServiceInstallationState.CreatePending(serviceId)
            : ServiceInstallationState.CreatePending(serviceId).Complete();
    }

    internal static void Validate(ServiceInstallationEntity entity)
    {
        if (!ServiceId.TryParse(entity.ServiceId, out var serviceId)
            || serviceId is null
            || !string.Equals(serviceId.Value, entity.ServiceId, StringComparison.Ordinal))
        {
            throw new ServiceInstallationStoreException(
                "installation.entity_invalid",
                "The stored installation service identifier is invalid.");
        }

        if (!Enum.IsDefined(entity.Status))
        {
            throw new ServiceInstallationStoreException(
                "installation.entity_invalid",
                "The stored installation status value is invalid.");
        }

        if (entity.CreatedAtUtc == default || entity.Version < 1)
        {
            throw new ServiceInstallationStoreException(
                "installation.state_invariant_violation",
                "The stored installation state has invalid required values.");
        }

        if (entity.Status == InstallationStatus.PendingSetup && entity.CompletedAtUtc.HasValue)
        {
            throw new ServiceInstallationStoreException(
                "installation.state_invariant_violation",
                "The pending installation state has a completion timestamp.");
        }

        if (entity.Status == InstallationStatus.Completed && !entity.CompletedAtUtc.HasValue)
        {
            throw new ServiceInstallationStoreException(
                "installation.state_invariant_violation",
                "The completed installation state has no completion timestamp.");
        }

        if (entity.CompletedAtUtc.HasValue && entity.CompletedAtUtc < entity.CreatedAtUtc)
        {
            throw new ServiceInstallationStoreException(
                "installation.state_invariant_violation",
                "The stored installation state has invalid date order.");
        }
    }
}
