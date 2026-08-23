namespace ServiceMantle.Audit;

/// <summary>
/// A validated, bounded filter and paging request for querying management audit records.
/// Instances are only created through <see cref="Create"/>, which enforces explicit limits on page
/// size, sorting, time range, and filter values so queries stay predictable and safe to execute.
/// </summary>
public sealed record ManagementAuditQuery
{
    /// <summary>
    /// The default number of records returned per page.
    /// </summary>
    public const int DefaultPageSize = 50;

    /// <summary>
    /// The maximum number of records allowed per page.
    /// </summary>
    public const int MaxPageSize = 200;

    /// <summary>
    /// The maximum span, in days, allowed between <see cref="FromUtc"/> and <see cref="ToUtc"/> when
    /// both are supplied.
    /// </summary>
    public const int MaxQueryRangeDays = 366;

    /// <summary>
    /// Gets the action filter, or null to match any action.
    /// </summary>
    public ManagementAuditAction? Action { get; }

    /// <summary>
    /// Gets the target type filter, or null to match any target type.
    /// </summary>
    public ManagementAuditTargetType? TargetType { get; }

    /// <summary>
    /// Gets the target identifier filter, or null to match any target identifier.
    /// </summary>
    public string? TargetId { get; }

    /// <summary>
    /// Gets the operator identifier filter, or null to match any operator.
    /// </summary>
    public string? OperatorId { get; }

    /// <summary>
    /// Gets the inclusive lower bound of the occurrence time range, or null for no lower bound.
    /// </summary>
    public DateTimeOffset? FromUtc { get; }

    /// <summary>
    /// Gets the inclusive upper bound of the occurrence time range, or null for no upper bound.
    /// </summary>
    public DateTimeOffset? ToUtc { get; }

    /// <summary>
    /// Gets the requested one-based page number.
    /// </summary>
    public int Page { get; }

    /// <summary>
    /// Gets the requested page size.
    /// </summary>
    public int PageSize { get; }

    /// <summary>
    /// Gets the requested sort order.
    /// </summary>
    public ManagementAuditSortOrder SortOrder { get; }

    private ManagementAuditQuery(
        ManagementAuditAction? action,
        ManagementAuditTargetType? targetType,
        string? targetId,
        string? operatorId,
        DateTimeOffset? fromUtc,
        DateTimeOffset? toUtc,
        int page,
        int pageSize,
        ManagementAuditSortOrder sortOrder)
    {
        Action = action;
        TargetType = targetType;
        TargetId = targetId;
        OperatorId = operatorId;
        FromUtc = fromUtc;
        ToUtc = toUtc;
        Page = page;
        PageSize = pageSize;
        SortOrder = sortOrder;
    }

    /// <summary>
    /// Creates a validated, bounded audit query.
    /// </summary>
    /// <exception cref="ManagementAuditException">A filter, page, page size, sort order, or time range value is invalid.</exception>
    public static ManagementAuditQuery Create(
        ManagementAuditAction? action = null,
        ManagementAuditTargetType? targetType = null,
        string? targetId = null,
        string? operatorId = null,
        DateTimeOffset? fromUtc = null,
        DateTimeOffset? toUtc = null,
        int page = 1,
        int pageSize = DefaultPageSize,
        ManagementAuditSortOrder sortOrder = ManagementAuditSortOrder.Newest)
    {
        if (page < 1)
        {
            throw new ManagementAuditException(
                "audit.query_page_invalid",
                "The audit query page number must be at least 1.");
        }

        if (pageSize is < 1 or > MaxPageSize)
        {
            throw new ManagementAuditException(
                "audit.query_page_size_invalid",
                $"The audit query page size must be between 1 and {MaxPageSize}.");
        }

        if (!Enum.IsDefined(sortOrder))
        {
            throw new ManagementAuditException(
                "audit.query_sort_order_invalid",
                "The audit query sort order value is not defined.");
        }

        if (fromUtc.HasValue && toUtc.HasValue)
        {
            if (fromUtc.Value > toUtc.Value)
            {
                throw new ManagementAuditException(
                    "audit.query_time_range_invalid",
                    "The audit query time range start must not be after its end.");
            }

            if (toUtc.Value - fromUtc.Value > TimeSpan.FromDays(MaxQueryRangeDays))
            {
                throw new ManagementAuditException(
                    "audit.query_time_range_too_wide",
                    $"The audit query time range must not exceed {MaxQueryRangeDays} days.");
            }
        }

        var cleanedTargetId = AuditTextSanitizer.Clean(
            targetId,
            ManagementAuditTarget.MaxTargetIdLength,
            "audit.query_filter_invalid",
            "target identifier filter");
        var cleanedOperatorId = AuditTextSanitizer.Clean(
            operatorId,
            ManagementAuditOperator.MaxOperatorIdLength,
            "audit.query_filter_invalid",
            "operator identifier filter");

        return new ManagementAuditQuery(
            action,
            targetType,
            cleanedTargetId,
            cleanedOperatorId,
            fromUtc,
            toUtc,
            page,
            pageSize,
            sortOrder);
    }
}
