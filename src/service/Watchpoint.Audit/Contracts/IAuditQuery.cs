using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Audit.Contracts;

/// <summary>Reads the audit trail (spec AUD §7). Query results are not filtered by device scope: access is <c>admin.audit.view</c> only.</summary>
public interface IAuditQuery
{
    /// <summary>Queries entries with keyset paging on <c>(occurred_at, id)</c>.</summary>
    /// <param name="query">The filter.</param>
    /// <param name="page">The page request.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The page, or <c>audit.query.range_too_large</c> / <c>common.value.invalid_format</c> for a bad cursor.</returns>
    Task<Result<Page<AuditEntry>>> QueryAsync(AuditQuery query, PageRequest page, CancellationToken ct);

    /// <summary>Gets one entry.</summary>
    /// <param name="id">The store id.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The entry, or <see langword="null"/>.</returns>
    Task<AuditEntry?> GetAsync(long id, CancellationToken ct);
}
