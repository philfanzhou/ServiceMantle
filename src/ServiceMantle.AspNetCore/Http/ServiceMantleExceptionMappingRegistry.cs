namespace ServiceMantle.Http;

internal interface IServiceMantleExceptionMappingRegistration
{
    Type ExceptionType { get; }

    int StatusCode { get; }

    string? ErrorCode { get; }

    string? Title { get; }

    IReadOnlyList<ServiceMantleProblemExtensionFactory> ExtensionFactories { get; }
}

internal sealed class ServiceMantleExceptionMappingRegistration<TException>
    : IServiceMantleExceptionMappingRegistration
    where TException : Exception
{
    private readonly IReadOnlyDictionary<
        string,
        Func<TException, object?>>? extensionFields;

    internal ServiceMantleExceptionMappingRegistration(
        int statusCode,
        string? errorCode,
        string? title,
        IReadOnlyDictionary<string, Func<TException, object?>>? extensionFields)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Title = title;
        this.extensionFields = extensionFields;
    }

    public Type ExceptionType => typeof(TException);

    public int StatusCode { get; }

    public string? ErrorCode { get; }

    public string? Title { get; }

    public IReadOnlyList<ServiceMantleProblemExtensionFactory> ExtensionFactories =>
        CreateExtensionFactories();

    private IReadOnlyList<ServiceMantleProblemExtensionFactory> CreateExtensionFactories()
    {
        if (extensionFields is null)
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var factories = new List<ServiceMantleProblemExtensionFactory>();
        foreach (var field in extensionFields)
        {
            var name = ServiceMantleProblemValue.ValidateExtensionName(
                field.Key,
                nameof(extensionFields));
            ArgumentNullException.ThrowIfNull(field.Value, nameof(extensionFields));
            if (!names.Add(name))
            {
                throw new ArgumentException(
                    "A Problem Details extension field is duplicated.",
                    nameof(extensionFields));
            }

            factories.Add(new ServiceMantleProblemExtensionFactory(
                name,
                exception => field.Value((TException)exception),
                field.Value));
        }

        return factories
            .OrderBy(factory => factory.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

internal sealed record ServiceMantleProblemExtensionFactory(
    string Name,
    Func<Exception, object?> GetValue,
    Delegate RegistrationDelegate);

internal sealed class ServiceMantleExceptionMapping
{
    internal ServiceMantleExceptionMapping(
        Type exceptionType,
        int statusCode,
        string errorCode,
        string title,
        IReadOnlyList<ServiceMantleProblemExtensionFactory> extensionFactories)
    {
        ExceptionType = exceptionType;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Title = title;
        TypeUri = ServiceMantleProblemDetailsDefaults.TypeUriPrefix + errorCode;
        ExtensionFactories = extensionFactories;
    }

    internal Type ExceptionType { get; }

    internal int StatusCode { get; }

    internal string ErrorCode { get; }

    internal string Title { get; }

    internal string TypeUri { get; }

    internal IReadOnlyList<ServiceMantleProblemExtensionFactory> ExtensionFactories { get; }
}

internal sealed class ServiceMantleExceptionMappingRegistry
{
    private readonly IReadOnlyDictionary<Type, ServiceMantleExceptionMapping> mappings;

    public ServiceMantleExceptionMappingRegistry(
        IEnumerable<IServiceMantleExceptionMappingRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var validated = new Dictionary<Type, ServiceMantleExceptionMapping>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            var mapping = Validate(registration);
            if (validated.TryGetValue(mapping.ExceptionType, out var existing))
            {
                if (!IsSameRegistration(existing, mapping))
                {
                    throw new InvalidOperationException(
                        "Conflicting ServiceMantle exception mappings are registered for the same exception type.");
                }

                continue;
            }

            validated.Add(mapping.ExceptionType, mapping);
        }

        mappings = validated;
    }

    internal bool TryGet(Type exceptionType, out ServiceMantleExceptionMapping? mapping) =>
        mappings.TryGetValue(exceptionType, out mapping);

    private static ServiceMantleExceptionMapping Validate(
        IServiceMantleExceptionMappingRegistration registration)
    {
        if (registration.StatusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registration),
                "A Problem Details exception mapping status must be between 400 and 599.");
        }

        var errorCode = ServiceMantleProblemValue.ValidateErrorCode(
            registration.ErrorCode!,
            nameof(registration));
        var title = ServiceMantleProblemValue.ValidateTitle(
            registration.Title!,
            nameof(registration));
        var extensionFactories = registration.ExtensionFactories;

        return new ServiceMantleExceptionMapping(
            registration.ExceptionType,
            registration.StatusCode,
            errorCode,
            title,
            extensionFactories);
    }

    private static bool IsSameRegistration(
        ServiceMantleExceptionMapping left,
        ServiceMantleExceptionMapping right)
    {
        if (left.StatusCode != right.StatusCode ||
            !string.Equals(left.ErrorCode, right.ErrorCode, StringComparison.Ordinal) ||
            !string.Equals(left.Title, right.Title, StringComparison.Ordinal) ||
            left.ExtensionFactories.Count != right.ExtensionFactories.Count)
        {
            return false;
        }

        for (var index = 0; index < left.ExtensionFactories.Count; index++)
        {
            var leftFactory = left.ExtensionFactories[index];
            var rightFactory = right.ExtensionFactories[index];
            if (!string.Equals(leftFactory.Name, rightFactory.Name, StringComparison.Ordinal) ||
                !Equals(leftFactory.RegistrationDelegate, rightFactory.RegistrationDelegate))
            {
                return false;
            }
        }

        return true;
    }
}
