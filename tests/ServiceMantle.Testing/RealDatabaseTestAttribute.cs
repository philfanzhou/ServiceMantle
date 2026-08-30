using Xunit.v3;

namespace ServiceMantle.Testing;

/// <summary>
/// Classifies a test as requiring a real database product.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, AllowMultiple = false, Inherited = true)]
public sealed class RealDatabaseTestAttribute(RealDatabaseProvider provider) : Attribute, ITraitAttribute
{
    public const string CategoryTraitName = "Category";
    public const string CategoryTraitValue = "RealDatabase";
    public const string ProviderTraitName = "Provider";

    public RealDatabaseProvider Provider { get; } = provider;

    public IReadOnlyCollection<KeyValuePair<string, string>> GetTraits() =>
    [
        new(CategoryTraitName, CategoryTraitValue),
        new(ProviderTraitName, Provider.ToString())
    ];
}
