using Microsoft.Extensions.Logging;

namespace Watchpoint.Configuration;

/// <summary>Log messages of the configuration module, event ids 1300–1399 (spec CFG §12).</summary>
internal static partial class ConfigurationLog
{
    [LoggerMessage(EventId = 1301, Level = LogLevel.Information, Message = "Draft {DraftId} published as version {Version} by {UserId}: {Summary}")]
    public static partial void Published(ILogger logger, Guid draftId, long version, Guid userId, string summary);

    [LoggerMessage(EventId = 1302, Level = LogLevel.Warning, Message = "Publish of draft {DraftId} rejected: {Code}")]
    public static partial void Rejected(ILogger logger, Guid draftId, string code);

    [LoggerMessage(EventId = 1303, Level = LogLevel.Error, Message = "Publish of draft {DraftId} failed and was rolled back: {Error}")]
    public static partial void Failed(ILogger logger, Exception? exception, Guid draftId, string error);

    [LoggerMessage(EventId = 1304, Level = LogLevel.Error, Message = "CompleteApply failed in module {Module} for version {Version} (in-memory state will be reloaded)")]
    public static partial void CompleteFailed(ILogger logger, Exception exception, string module, long version);

    [LoggerMessage(EventId = 1305, Level = LogLevel.Error, Message = "Reload of module {Module} failed for version {Version}")]
    public static partial void ReloadFailed(ILogger logger, Exception exception, string module, long version);

    [LoggerMessage(EventId = 1306, Level = LogLevel.Error, Message = "Version {Version} committed but its config.published record or audit failed")]
    public static partial void AnnounceFailed(ILogger logger, Exception exception, long version);

    [LoggerMessage(EventId = 1307, Level = LogLevel.Warning, Message = "Scheduled publish of draft {DraftId} failed: {Code}")]
    public static partial void ScheduledFailed(ILogger logger, Guid draftId, string code);

    [LoggerMessage(EventId = 1310, Level = LogLevel.Information, Message = "Rollback draft {DraftId} created from version {Version}")]
    public static partial void RollbackCreated(ILogger logger, Guid draftId, long version);
}
