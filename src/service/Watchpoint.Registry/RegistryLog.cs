using Microsoft.Extensions.Logging;

namespace Watchpoint.Registry;

internal static partial class RegistryLog
{
    [LoggerMessage(EventId = 1, Level = LogLevel.Information, Message = "Registry initialized with {DeviceCount} devices across {ServerCount} servers")]
    public static partial void Initialized(ILogger logger, int deviceCount, int serverCount);

    [LoggerMessage(EventId = 2, Level = LogLevel.Information, Message = "Registry batch applied: {ChangeCount} changes in {DurationMs} ms")]
    public static partial void BatchApplied(ILogger logger, int changeCount, double durationMs);

    [LoggerMessage(EventId = 3, Level = LogLevel.Warning, Message = "Registry optimistic concurrency conflict: {ErrorMessage}")]
    public static partial void ConcurrencyConflict(ILogger logger, string errorMessage);

    [LoggerMessage(EventId = 4, Level = LogLevel.Warning, Message = "Registry invariant violation: {Rule} - {ErrorMessage}")]
    public static partial void InvariantViolation(ILogger logger, string rule, string errorMessage);

    [LoggerMessage(EventId = 1504, Level = LogLevel.Warning, Message = "Registry site row {StoredSiteId} differs from Site:Id {ConfiguredSiteId}: keeping the stored row; set Site:Id to the stored id so the token issuer matches the site")]
    public static partial void SiteIdMismatch(ILogger logger, Guid storedSiteId, Guid configuredSiteId);

    [LoggerMessage(EventId = 1510, Level = LogLevel.Information, Message = "Reconciled connection {ConnectionId}: {Updated} updated, {Orphaned} orphaned, {Restored} restored, {New} new, {Imported} imported")]
    public static partial void ConnectionReconciled(ILogger logger, Guid connectionId, int updated, int orphaned, int restored, int @new, int imported);

    [LoggerMessage(EventId = 1511, Level = LogLevel.Warning, Message = "Reconcile warning {Code} for {NativeId}")]
    public static partial void ReconcileWarning(ILogger logger, string code, string nativeId);
}
