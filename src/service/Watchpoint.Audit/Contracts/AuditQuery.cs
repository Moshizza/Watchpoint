namespace Watchpoint.Audit.Contracts;

/// <summary>Filter of an audit query (spec AUD §7).</summary>
/// <param name="From">Inclusive start of the time range.</param>
/// <param name="To">Exclusive end of the time range; at most 366 days after <paramref name="From"/>.</param>
/// <param name="Actors">Actor ids.</param>
/// <param name="ActionPrefixes">Action prefixes, e.g. <c>rights.</c> or <c>identity.signedIn</c>.</param>
/// <param name="ObjectType">Type of an affected object.</param>
/// <param name="ObjectId">Id of an affected object.</param>
/// <param name="Outcome">Outcome.</param>
/// <param name="MinSeverity">Minimum severity.</param>
/// <param name="CorrelationId">Correlation id.</param>
/// <param name="Text">Text in the reason.</param>
/// <param name="Sort">Sort order by time.</param>
public sealed record AuditQuery(
    DateTimeOffset From,
    DateTimeOffset To,
    IReadOnlyList<Guid>? Actors = null,
    IReadOnlyList<string>? ActionPrefixes = null,
    string? ObjectType = null,
    Guid? ObjectId = null,
    AuditOutcome? Outcome = null,
    AuditSeverity? MinSeverity = null,
    Guid? CorrelationId = null,
    string? Text = null,
    SortDirection Sort = SortDirection.Descending);
