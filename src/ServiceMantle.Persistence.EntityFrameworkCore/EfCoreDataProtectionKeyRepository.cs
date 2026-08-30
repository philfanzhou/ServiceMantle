using System.Collections.ObjectModel;
using System.Globalization;
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
    private const int MaximumElementIdLength = 64;
    private const string KeyElementName = "key";
    private const string RevocationElementName = "revocation";
    private const string RevocationDateElementName = "revocationDate";
    private const string ProtectionPurposePrefix = "data_protection.repository_xml.";

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

        var elementId = ReadElementId(element, friendlyName);
        string encryptedXml;
        try
        {
            var rootKey = rootKeyResolver();
            encryptedXml = CreateProtector(elementId).Protect(
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
                KeyId = elementId,
                EncryptedXml = encryptedXml,
            });
            dbContext.SaveChanges();
            transaction.Commit();
        }
        catch (DbUpdateException)
        {
            SafeRollback(transaction);
            if (RowExists(elementId))
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
            ReadElementId(element, entity.KeyId);

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

    private SensitiveValueProtector CreateProtector(string elementId) =>
        new(serviceId, ProtectionPurposePrefix + elementId);

    private static string ReadElementId(XElement element, string friendlyName)
    {
        try
        {
            if (!IsSafeElementId(friendlyName) || element.Attribute("version")?.Value != "1")
            {
                throw InvalidElement();
            }

            string expectedFriendlyName;
            if (element.Name == KeyElementName)
            {
                var keyId = Guid.ParseExact(element.Attribute("id")!.Value, "D");
                expectedFriendlyName = $"key-{keyId:D}";
            }
            else if (element.Name == RevocationElementName)
            {
                var revocationDate = (DateTimeOffset)element.Element(RevocationDateElementName)!;
                var revokedKeyId = element.Element(KeyElementName)!.Attribute("id")!.Value;
                expectedFriendlyName = revokedKeyId == "*"
                    ? "revocation-" + revocationDate.UtcDateTime.ToString(
                        "yyyyMMddTHHmmssFFFFFFFZ",
                        CultureInfo.InvariantCulture)
                    : $"revocation-{Guid.ParseExact(revokedKeyId, "D"):D}";
            }
            else
            {
                throw InvalidElement();
            }

            if (!string.Equals(friendlyName, expectedFriendlyName, StringComparison.Ordinal))
            {
                throw InvalidElement();
            }

            return expectedFriendlyName;
        }
        catch (DataProtectionKeyRepositoryException)
        {
            throw;
        }
        catch
        {
            throw InvalidElement();
        }
    }

    private static bool IsSafeElementId(string elementId) =>
        elementId.Length is > 0 and <= MaximumElementIdLength &&
        elementId.All(character =>
            character is '-' or '_' or >= '0' and <= '9' or >= 'A' and <= 'Z' or >= 'a' and <= 'z');

    private static DataProtectionKeyRepositoryException InvalidElement() =>
        new(
            WellKnownDataProtectionKeyRepositoryErrorCodes.InvalidElement,
            "The Data Protection repository element is invalid.");

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
            "The Data Protection repository element identifier already exists for this service.");

    private static DataProtectionKeyRepositoryException RootKeyUnavailable() =>
        new(
            WellKnownDataProtectionKeyRepositoryErrorCodes.RootKeyUnavailable,
            "The external root key could not protect the Data Protection repository element.");

    private static DataProtectionKeyRepositoryException DecryptionFailed() =>
        new(
            WellKnownDataProtectionKeyRepositoryErrorCodes.DecryptionFailed,
            "A stored Data Protection repository element could not be authenticated or decoded.");

    private static DataProtectionKeyRepositoryException StorageFailure() =>
        new(
            WellKnownDataProtectionKeyRepositoryErrorCodes.StorageError,
            "The Data Protection key repository operation failed.");
}
