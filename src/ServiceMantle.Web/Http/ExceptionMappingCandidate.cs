namespace ServiceMantle.Web.Http;

/// <summary>
/// A single pre-declared outcome of a conditional Problem Details exception mapping.
/// </summary>
/// <typeparam name="TException">The exact exception type handled by the mapping.</typeparam>
/// <remarks>
/// Candidates are evaluated in registration order and the first candidate whose condition holds
/// produces the response. A candidate without a condition applies unconditionally. Conditions,
/// status codes, error codes, titles, extension whitelists, and Retry-After values are declared
/// up front and validated when the host starts; nothing is projected from the exception into
/// response headers.
/// </remarks>
public sealed class ExceptionMappingCandidate<TException>
    where TException : Exception
{
    private readonly Func<TException, bool>? condition;
    private readonly IReadOnlyDictionary<string, Func<TException, object?>>? extensionFields;

    /// <summary>
    /// Declares one mapping candidate for the conditional exception mapping registration.
    /// </summary>
    /// <param name="statusCode">The fixed HTTP error status returned by this candidate.</param>
    /// <param name="errorCode">The stable error code used in the response and type URI.</param>
    /// <param name="title">The fixed, public-safe Problem Details title.</param>
    /// <param name="condition">
    /// An optional predicate over the caught exception. When omitted, the candidate applies
    /// unconditionally. Predicates run once per request while the response has not started and
    /// must not have side effects.
    /// </param>
    /// <param name="extensionFields">
    /// Optional explicitly named extension value factories. The names form this candidate's
    /// whitelist; values are supplied by the consuming service and are not sanitized by
    /// ServiceMantle.
    /// </param>
    /// <param name="retryAfterSeconds">
    /// An optional fixed <c>Retry-After</c> value in whole delta-seconds between 1 and 86400,
    /// written as an invariant decimal string while the response has not started.
    /// </param>
    public ExceptionMappingCandidate(
        int statusCode,
        string errorCode,
        string title,
        Func<TException, bool>? condition = null,
        IReadOnlyDictionary<string, Func<TException, object?>>? extensionFields = null,
        int? retryAfterSeconds = null)
    {
        StatusCode = statusCode;
        ErrorCode = errorCode;
        Title = title;
        this.condition = condition;
        this.extensionFields = extensionFields;
        RetryAfterSeconds = retryAfterSeconds;
    }

    internal int StatusCode { get; }

    internal string? ErrorCode { get; }

    internal string? Title { get; }

    internal Func<TException, bool>? Condition => condition;

    internal IReadOnlyDictionary<string, Func<TException, object?>>? ExtensionFields =>
        extensionFields;

    internal int? RetryAfterSeconds { get; }
}
