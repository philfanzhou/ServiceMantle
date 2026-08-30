using System.Collections.ObjectModel;
using System.Xml.Linq;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage;
using ServiceMantle.Configuration;

namespace ServiceMantle.Persistence.EntityFrameworkCore;

/// <summary>
/// Stores ASP.NET Core Data Protection key XML in a service-isolated encrypted EF Core repository.
/// </summary>
/// <remarks>
/// Each call uses a dedicated DbContext. The root-key callback is invoked once per operation; the
/// returned key material is neither persisted nor included in repository diagnostics.
/// </remarks>
public sealed class EfCoreDataProtectionKeyRepository<TDbContext> : IXmlRepository
    where TDbContext : DbContext
{
    private const string ProtectionPurposePrefix = "data_protection.key_xml.";

    private readonly IDbContextFactory<TDbContext> dbContextFactory;
    private readonly ServiceId serviceId;
    private readonly Func<string> rootKeyResolver;

    /// <summary>Initializes an encrypted key repository for one service.</summary>
    /// <param name="dbContextFactory">The factory for dedicated persistence contexts.</param>
    /// <param name="serviceId">The service that owns the key ring.</param>
    /// <param name="rootKeyResolver">A callback that returns the external Bootstrap root key.</param>
    public EfCoreDataProtectionKeyRepository(
        IDbContextFactory<TDbContext> dbContextFactory,
        ServiceId serviceId,
        Func<string> rootKeyResolver)
    {
        ArgumentNullException.ThrowIfNull(dbContextFactory);
        ArgumentNullException.ThrowIfNull(serviceId);
        ArgumentNullException.ThrowIfNull(rootKeyResolver);

        this.dbContextFactory = dbContextFactory;
        this.serviceId = serviceId;
        this.rootKeyResolver = rootKeyResolver;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<XElement> GetAllElements()
    {
        try
        {
            using var dbContext = dbContextFactory.CreateDbContext();
            var entities = dbContext.Set<DataProtectionKeyEntity>()
                .AsNoTracking()
                .Where(item => item.ServiceId == serviceId.Value)
                .OrderBy(item => item.KeyId)
                .ToList();
            if (entities.Count == 0)
            {
                return Array.Empty<XElement>();
            }

            var rootKey = ResolveRootKeyForRead();
            var elements = new List<XElement>(entities.Count);
            foreach (var entity in entities)
            {
                elements.Add(DecryptElement(entity, rootKey));
            }

            return new ReadOnlyCollection<XElement>(elements);
        }
        catch (DataProtectionKeyRepositoryException)
        {
            throw;
        }
        catch
        {
            throw StorageFailure();
        }
    }

    /// <inheritdoc />
    public void StoreElement(XElement element, string friendlyName)
    {
        ArgumentNullException.ThrowIfNull(element);
        ArgumentNullException.ThrowIfNull(friendlyName);

        var keyId = ReadKeyId(element);
        string encryptedXml;
        try
        {
            var rootKey = rootKeyResolver();
            encryptedXml = CreateProtector(keyId).Protect(
                element.ToString(SaveOptions.DisableFormatting),
                rootKey);
        }
        catch
        {
            throw RootKeyUnavailable();
        }

        TDbContext? dbContext = null;
        IDbContextTransaction? transaction = null;
        try
        {
            dbContext = dbContextFactory.CreateDbContext();
            transaction = dbContext.Database.BeginTransaction();
            dbContext.Set<DataProtectionKeyEntity>().Add(new DataProtectionKeyEntity
            {
                ServiceId = serviceId.Value,
                KeyId = keyId,
                EncryptedXml = encryptedXml,
            });
            dbContext.SaveChanges();
            transaction.Commit();
        }
        catch (DbUpdateException)
        {
            SafeRollback(transaction);
            if (RowExists(keyId))
            {
                throw DuplicateKey();
            }

            throw StorageFailure();
        }
        catch (DataProtectionKeyRepositoryException)
        {
            SafeRollback(transaction);
            throw;
        }
        catch
        {
            SafeRollback(transaction);
            throw StorageFailure();
        }
        finally
        {
            SafeDispose(transaction);
            SafeDispose(dbContext);
        }
    }

    /// <summary>Returns safe repository context without root-key, XML, or ciphertext material.</summary>
    public override string ToString() =>
        $"EfCoreDataProtectionKeyRepository(ServiceId={serviceId.Value})";

    private XElement DecryptElement(DataProtectionKeyEntity entity, string rootKey)
    {
        try
        {
            var plaintext = CreateProtector(entity.KeyId).Unprotect(entity.EncryptedXml, rootKey);
            var element = XElement.Parse(plaintext, LoadOptions.PreserveWhitespace);
            if (!string.Equals(ReadKeyId(element), entity.KeyId, StringComparison.Ordinal))
            {
                throw DecryptionFailed();
            }

            return element;
        }
        catch
        {
            throw DecryptionFailed();
        }
    }

    private string ResolveRootKeyForRead()
    {
        try
        {
            var rootKey = rootKeyResolver();
            if (string.IsNullOrWhiteSpace(rootKey))
            {
                throw DecryptionFailed();
            }

            return rootKey;
        }
        catch (DataProtectionKeyRepositoryException)
        {
            throw;
        }
        catch
        {
            throw DecryptionFailed();
        }
    }

    private bool RowExists(string keyId)
    {
        try
        {
            using var dbContext = dbContextFactory.CreateDbContext();
            return dbContext.Set<DataProtectionKeyEntity>()
                .AsNoTracking()
                .Any(item => item.ServiceId == serviceId.Value && item.KeyId == keyId);
        }
        catch
        {
            return false;
        }
    }

    private SensitiveValueProtector CreateProtector(string keyId) =>
        new(serviceId, ProtectionPurposePrefix + keyId);

    private static string ReadKeyId(XElement element)
    {
        var id = element.Name == "key" ? element.Attribute("id")?.Value : null;
        if (id is null || !Guid.TryParseExact(id, "D", out var keyId))
        {
            throw new DataProtectionKeyRepositoryException(
                WellKnownDataProtectionKeyRepositoryErrorCodes.InvalidElement,
                "The Data Protection key element is invalid.");
        }

        return keyId.ToString("D");
    }

    private static void SafeRollback(IDbContextTransaction? transaction)
    {
        try
        {
            transaction?.Rollback();
        }
        catch
        {
            // Cleanup failures never replace the safe primary classification.
        }
    }

    private static void SafeDispose(IDisposable? resource)
    {
        try
        {
            resource?.Dispose();
        }
        catch
        {
            // Cleanup failures never replace the safe primary classification.
        }
    }

    private static DataProtectionKeyRepositoryException DuplicateKey() =>
        new(
            WellKnownDataProtectionKeyRepositoryErrorCodes.DuplicateKey,
            "The Data Protection key identifier already exists for this service.");

    private static DataProtectionKeyRepositoryException RootKeyUnavailable() =>
        new(
            WellKnownDataProtectionKeyRepositoryErrorCodes.RootKeyUnavailable,
            "The external root key could not protect the Data Protection key.");

    private static DataProtectionKeyRepositoryException DecryptionFailed() =>
        new(
            WellKnownDataProtectionKeyRepositoryErrorCodes.DecryptionFailed,
            "A stored Data Protection key could not be authenticated or decoded.");

    private static DataProtectionKeyRepositoryException StorageFailure() =>
        new(
            WellKnownDataProtectionKeyRepositoryErrorCodes.StorageError,
            "The Data Protection key repository operation failed.");
}
