using Microsoft.Extensions.Logging;

namespace Watchpoint.Storage;

/// <summary>Log events of the storage module (spec STO §10, event ids 1200–1299).</summary>
internal static partial class StorageLog
{
    [LoggerMessage(EventId = 1201, Level = LogLevel.Information, Message = "Database {Key} ({Provider} {Version}) reachable")]
    public static partial void DatabaseReachable(ILogger logger, string key, string provider, string version);

    [LoggerMessage(EventId = 1202, Level = LogLevel.Warning, Message = "Database {Key} unreachable, retrying in {DelayMs} ms ({Target}: {Reason})")]
    public static partial void DatabaseUnreachable(ILogger logger, string key, long delayMs, string target, string reason);

    [LoggerMessage(EventId = 1203, Level = LogLevel.Warning, Message = "TimescaleDB extension not available on {Key}; history uses plain partitioning")]
    public static partial void TimescaleMissing(ILogger logger, string key);

    [LoggerMessage(EventId = 1204, Level = LogLevel.Warning, Message = "Storage settings: {Warning}")]
    public static partial void SettingsWarning(ILogger logger, string warning);

    [LoggerMessage(EventId = 1210, Level = LogLevel.Information, Message = "Applied migration {Migration} for module {Module}")]
    public static partial void MigrationApplied(ILogger logger, string migration, string module);

    [LoggerMessage(EventId = 1211, Level = LogLevel.Error, Message = "Migration {Migration} for module {Module} failed")]
    public static partial void MigrationFailed(ILogger logger, Exception exception, string migration, string module);

    [LoggerMessage(EventId = 1212, Level = LogLevel.Information, Message = "Waiting for the migration lock of database {Key}")]
    public static partial void MigrationLockWaiting(ILogger logger, string key);
}
