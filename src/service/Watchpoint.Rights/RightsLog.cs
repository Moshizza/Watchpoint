using Microsoft.Extensions.Logging;

namespace Watchpoint.Rights;

internal static partial class RightsLog
{
    [LoggerMessage(EventId = 2801, Level = LogLevel.Information, Message = "Rights changed for {Count} users (version {Version})")]
    public static partial void RightsChanged(ILogger logger, int count, long version);

    [LoggerMessage(EventId = 2820, Level = LogLevel.Warning, Message = "Escalation denied for {Actor}: {Permission} ({Reason})")]
    public static partial void EscalationDenied(ILogger logger, Guid actor, string permission, string reason);

    [LoggerMessage(EventId = 2802, Level = LogLevel.Warning, Message = "Could not publish rights.changed")]
    public static partial void PublishFailed(ILogger logger, Exception exception);
}
