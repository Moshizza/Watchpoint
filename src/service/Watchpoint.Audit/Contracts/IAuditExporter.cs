using Watchpoint.Kernel.Results;

namespace Watchpoint.Audit.Contracts;

/// <summary>Exports audit entries (spec AUD §9); the export is itself audited as <c>audit.exported</c>.</summary>
public interface IAuditExporter
{
    /// <summary>Streams the entries of a query as CSV with localised action labels.</summary>
    /// <param name="query">The filter.</param>
    /// <param name="output">The stream to write to.</param>
    /// <param name="language">The language of labels.</param>
    /// <param name="actor">Who exports.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The number of rows written, or <c>audit.query.range_too_large</c>.</returns>
    Task<Result<long>> ExportCsvAsync(AuditQuery query, Stream output, string language, AuditActor actor, CancellationToken ct);
}
