namespace ServiceMantle.Web.Http;

internal interface IExceptionMappingRegistration
{
    Type ExceptionType { get; }

    int StatusCode { get; }

    string? ErrorCode { get; }

    string? Title { get; }

    IReadOnlyList<ProblemExtensionFactory> ExtensionFactories { get; }
}

internal interface IConditionalExceptionMappingRegistration
{
    Type ExceptionType { get; }

    IReadOnlyList<ExceptionMappingVariant> Variants { get; }
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
        ProblemExtensionFactory.CreateList(extensionFields);
}

internal sealed class ConditionalExceptionMappingRegistration<TException>
    : IConditionalExceptionMappingRegistration
    where TException : Exception
{
    private readonly IReadOnlyList<ExceptionMappingCandidate<TException>> candidates;

    internal ConditionalExceptionMappingRegistration(
        IReadOnlyList<ExceptionMappingCandidate<TException>> candidates)
    {
        this.candidates = candidates;
    }

    public Type ExceptionType => typeof(TException);

    public IReadOnlyList<ExceptionMappingVariant> Variants =>
        candidates.Select(CreateVariant).ToArray();

    private static ExceptionMappingVariant CreateVariant(
        ExceptionMappingCandidate<TException> candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return new ExceptionMappingVariant(
            candidate.StatusCode,
            candidate.ErrorCode!,
            candidate.Title!,
            candidate.Condition is null
                ? null
                : exception => candidate.Condition((TException)exception),
            candidate.Condition,
            ProblemExtensionFactory.CreateList(candidate.ExtensionFields),
            candidate.RetryAfterSeconds);
    }
}

internal sealed record ProblemExtensionFactory(
    string Name,
    Func<Exception, object?> GetValue,
    Delegate RegistrationDelegate)
{
    internal static IReadOnlyList<ProblemExtensionFactory> CreateList<TException>(
        IReadOnlyDictionary<string, Func<TException, object?>>? extensionFields)
        where TException : Exception
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

/// <summary>
/// One pre-declared outcome of an exception mapping. A conditional mapping selects the first
/// variant whose condition holds; a fixed mapping has exactly one unconditional variant.
/// </summary>
internal sealed class ExceptionMappingVariant
{
    internal const int MinimumRetryAfterSeconds = 1;
    internal const int MaximumRetryAfterSeconds = 86400;

    internal ExceptionMappingVariant(
        int statusCode,
        string? errorCode,
        string? title,
        Func<Exception, bool>? matches,
        Delegate? registrationMatch,
        IReadOnlyList<ProblemExtensionFactory> extensionFactories,
        int? retryAfterSeconds)
    {
        if (statusCode is < 400 or > 599)
        {
            throw new ArgumentOutOfRangeException(
                nameof(statusCode),
                "A Problem Details exception mapping status must be between 400 and 599.");
        }

        var validatedErrorCode = ProblemValue.ValidateErrorCode(
            errorCode!,
            nameof(errorCode));
        var validatedTitle = ProblemValue.ValidateTitle(
            title!,
            nameof(title));
        if (retryAfterSeconds is < MinimumRetryAfterSeconds or > MaximumRetryAfterSeconds)
        {
            throw new ArgumentOutOfRangeException(
                nameof(retryAfterSeconds),
                "A Problem Details Retry-After value must be a whole number of seconds between " +
                $"{MinimumRetryAfterSeconds} and {MaximumRetryAfterSeconds}.");
        }

        StatusCode = statusCode;
        ErrorCode = validatedErrorCode;
        Title = validatedTitle;
        TypeUri = ProblemDetailsDefaults.TypeUriPrefix + validatedErrorCode;
        Matches = matches;
        RegistrationMatch = registrationMatch;
        ExtensionFactories = extensionFactories;
        RetryAfterSeconds = retryAfterSeconds;
    }

    internal int StatusCode { get; }

    internal string ErrorCode { get; }

    internal string Title { get; }

    internal string TypeUri { get; }

    internal Func<Exception, bool>? Matches { get; }

    internal Delegate? RegistrationMatch { get; }

    internal IReadOnlyList<ProblemExtensionFactory> ExtensionFactories { get; }

    internal int? RetryAfterSeconds { get; }
}

internal sealed class ExceptionMapping
{
    internal ExceptionMapping(
        Type exceptionType,
        bool isConditional,
        IReadOnlyList<ExceptionMappingVariant> variants)
    {
        ExceptionType = exceptionType;
        IsConditional = isConditional;
        Variants = variants;
    }

    internal Type ExceptionType { get; }

    internal bool IsConditional { get; }

    internal IReadOnlyList<ExceptionMappingVariant> Variants { get; }

    internal ExceptionMappingVariant? Select(Exception exception)
    {
        foreach (var variant in Variants)
        {
            if (variant.Matches is null || variant.Matches(exception))
            {
                return variant;
            }
        }

        return null;
    }
}

internal sealed class ExceptionMappingRegistry
{
    private readonly IReadOnlyDictionary<Type, ExceptionMapping> mappings;

    public ExceptionMappingRegistry(
        IEnumerable<IExceptionMappingRegistration> registrations,
        IEnumerable<IConditionalExceptionMappingRegistration> conditionalRegistrations)
    {
        ArgumentNullException.ThrowIfNull(registrations);
        ArgumentNullException.ThrowIfNull(conditionalRegistrations);

        var validated = new Dictionary<Type, ExceptionMapping>();
        foreach (var registration in registrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            var mapping = new ExceptionMapping(
                registration.ExceptionType,
                isConditional: false,
                [
                    new ExceptionMappingVariant(
                        registration.StatusCode,
                        registration.ErrorCode,
                        registration.Title,
                        matches: null,
                        registrationMatch: null,
                        registration.ExtensionFactories,
                        retryAfterSeconds: null),
                ]);
            AddRegistration(validated, mapping);
        }

        foreach (var registration in conditionalRegistrations)
        {
            ArgumentNullException.ThrowIfNull(registration);
            var mapping = new ExceptionMapping(
                registration.ExceptionType,
                isConditional: true,
                ValidateVariants(registration.Variants));
            AddRegistration(validated, mapping);
        }

        mappings = validated;
    }

    internal bool TryGet(Type exceptionType, out ExceptionMapping? mapping) =>
        mappings.TryGetValue(exceptionType, out mapping);

    private static void AddRegistration(
        Dictionary<Type, ExceptionMapping> validated,
        ExceptionMapping mapping)
    {
        if (validated.TryGetValue(mapping.ExceptionType, out var existing))
        {
            if (existing.IsConditional != mapping.IsConditional ||
                !IsSameMapping(existing, mapping))
            {
                throw new InvalidOperationException(
                    "Conflicting ServiceMantle exception mappings are registered for the same exception type.");
            }

            return;
        }

        validated.Add(mapping.ExceptionType, mapping);
    }

    private static IReadOnlyList<ExceptionMappingVariant> ValidateVariants(
        IReadOnlyList<ExceptionMappingVariant> variants)
    {
        if (variants.Count == 0)
        {
            throw new InvalidOperationException(
                "A conditional ServiceMantle exception mapping must declare at least one candidate.");
        }

        var unconditionalCandidates = 0;
        foreach (var variant in variants)
        {
            if (variant.Matches is null)
            {
                unconditionalCandidates++;
            }
        }

        if (unconditionalCandidates > 1)
        {
            throw new InvalidOperationException(
                "A conditional ServiceMantle exception mapping may declare at most one unconditional candidate.");
        }

        return variants;
    }

    private static bool IsSameMapping(
        ExceptionMapping left,
        ExceptionMapping right)
    {
        if (left.Variants.Count != right.Variants.Count)
        {
            return false;
        }

        for (var index = 0; index < left.Variants.Count; index++)
        {
            if (!IsSameVariant(left.Variants[index], right.Variants[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsSameVariant(
        ExceptionMappingVariant left,
        ExceptionMappingVariant right)
    {
        if (left.StatusCode != right.StatusCode ||
            !string.Equals(left.ErrorCode, right.ErrorCode, StringComparison.Ordinal) ||
            !string.Equals(left.Title, right.Title, StringComparison.Ordinal) ||
            left.RetryAfterSeconds != right.RetryAfterSeconds ||
            !Equals(left.RegistrationMatch, right.RegistrationMatch) ||
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
