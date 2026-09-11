namespace ServiceMantle.AspNetCore.Http;

internal interface IExceptionMappingRegistration
{
    Type ExceptionType { get; }

    int StatusCode { get; }

    string? ErrorCode { get; }

    string? Title { get; }

    IReadOnlyList<ProblemExtensionFactory> ExtensionFactories { get; }
}

internal sealed class ExceptionMappingRegistration<TException>
    : IExceptionMappingRegistration
    where TException : Exception
{
    private readonly IReadOnlyDictionary<
        string,
        Func<TException, object?>>? extensionFields;

    internal ExceptionMappingRegistration(
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

    public IReadOnlyList<ProblemExtensionFactory> ExtensionFactories =>
        CreateExtensionFactories();

    private IReadOnlyList<ProblemExtensionFactory> CreateExtensionFactories()
    {
        if (extensionFields is null)
        {
            return [];
        }

        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var factories = new List<ProblemExtensionFactory>();
        foreach (var field in extensionFields)
        {
            var name = ProblemValue.ValidateExtensionName(
                field.Key,
                nameof(extensionFields));
            ArgumentNullException.ThrowIfNull(field.Value, nameof(extensionFields));
            if (!names.Add(name))
            {
                throw new ArgumentException(
                    "A Problem Details extension field is duplicated.",
                    nameof(extensionFields));
            }

            factories.Add(new ProblemExtensionFactory(
                name,
                exception => field.Value((TException)exception),
                field.Value));
        }

        return factories
            .OrderBy(factory => factory.Name, StringComparer.Ordinal)
            .ToArray();
    }
}

internal sealed record ProblemExtensionFactory(
    string Name,
    Func<Exception, object?> GetValue,
    Delegate RegistrationDelegate);

internal sealed class ExceptionMapping
{
    internal ExceptionMapping(
        Type exceptionType,
        int statusCode,
        string errorCode,
        string title,
        IReadOnlyList<ProblemExtensionFactory> extensionFactories)
    {
        ExceptionType = exceptionType;
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Title = title;
        TypeUri = ProblemDetailsDefaults.TypeUriPrefix + errorCode;
        ExtensionFactories = extensionFactories;
    }

    internal Type ExceptionType { get; }

    internal int StatusCode { get; }

    internal string ErrorCode { get; }

    internal string Title { get; }

    internal string TypeUri { get; }

    internal IReadOnlyList<ProblemExtensionFactory> ExtensionFactories { get; }
}

internal sealed class ExceptionMappingRegistry
{
    private readonly IReadOnlyDictionary<Type, ExceptionMapping> mappings;

    public ExceptionMappingRegistry(
        IEnumerable<IExceptionMappingRegistration> registrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);

        var validated = new Dictionary<Type, ExceptionMapping>();
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

    internal bool TryGet(Type exceptionType, out ExceptionMapping? mapping) =>
        mappings.TryGetValue(exceptionType, out mapping);

    private static ExceptionMapping Validate(
        IExceptionMappingRegistration registration)
    {
        if (registration.StatusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(registration),
                "A Problem Details exception mapping status must be between 400 and 599.");
        }

        var errorCode = ProblemValue.ValidateErrorCode(
            registration.ErrorCode!,
            nameof(registration));
        var title = ProblemValue.ValidateTitle(
            registration.Title!,
            nameof(registration));
        var extensionFactories = registration.ExtensionFactories;

        return new ExceptionMapping(
            registration.ExceptionType,
            registration.StatusCode,
            errorCode,
            title,
            extensionFactories);
    }

    private static bool IsSameRegistration(
        ExceptionMapping left,
        ExceptionMapping right)
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
