using Microsoft.Extensions.Logging;

namespace Watchpoint.State;

/// <summary>
/// Log events of the state module (spec STA §11, event ids 1700–1799). Events 1701, 1710 and 1720 belong to the
/// snapshotter and are implemented with it (T1.12).
/// </summary>
internal static partial class StateLog
{
    [LoggerMessage(EventId = 1730, Level = LogLevel.Error, Message = "Aggregate drift detected for a scope: {Details}; corrected")]
    public static partial void AggregateDriftCorrected(ILogger logger, string details);

    [LoggerMessage(EventId = 1740, Level = LogLevel.Warning, Message = "Listener {Listener} overflowed")]
    public static partial void ListenerOverflowed(ILogger logger, string listener);
}
