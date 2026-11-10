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
}
