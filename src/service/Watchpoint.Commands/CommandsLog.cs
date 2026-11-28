using Microsoft.Extensions.Logging;

namespace Watchpoint.Commands;

/// <summary>Log messages of the commands module, event ids 2400–2499 (spec CMD §14).</summary>
internal static partial class CommandsLog
{
    [LoggerMessage(EventId = 2401, Level = LogLevel.Information, Message = "Command {CommandId} {Path} on {DeviceId} by {Issuer}: {State}")]
    public static partial void Transition(ILogger logger, Guid commandId, string path, Guid deviceId, Internal.IssuerText issuer, Contracts.CommandState state);

    [LoggerMessage(EventId = 2410, Level = LogLevel.Warning, Message = "Command {CommandId} failed: {Category} {VendorCode}")]
    public static partial void Failed(ILogger logger, Guid commandId, string category, string? vendorCode);

    [LoggerMessage(EventId = 2411, Level = LogLevel.Warning, Message = "Command {CommandId} timed out after {TimeoutMs} ms")]
    public static partial void TimedOut(ILogger logger, Guid commandId, long timeoutMs);

    [LoggerMessage(EventId = 2412, Level = LogLevel.Warning, Message = "Command {CommandId} not verified")]
    public static partial void NotVerified(ILogger logger, Guid commandId);

    [LoggerMessage(EventId = 2413, Level = LogLevel.Warning, Message = "Command {CommandId}: the connector threw instead of returning a result")]
    public static partial void ConnectorThrew(ILogger logger, Exception exception, Guid commandId);

    [LoggerMessage(EventId = 2430, Level = LogLevel.Information, Message = "Group {GroupId} completed: {Counters}")]
    public static partial void GroupCompleted(ILogger logger, Guid groupId, Contracts.CommandGroupCounters counters);

    [LoggerMessage(EventId = 2441, Level = LogLevel.Warning, Message = "Group {GroupId}: a group record could not be appended")]
    public static partial void GroupRecordFailed(ILogger logger, Exception exception, Guid groupId);

    [LoggerMessage(EventId = 2440, Level = LogLevel.Error, Message = "Command dispatch of connection {ConnectionId} failed")]
    public static partial void DispatchFailed(ILogger logger, Exception exception, Guid connectionId);

    [LoggerMessage(EventId = 2420, Level = LogLevel.Debug, Message = "Ignored late transition {Kind} for command {CommandId} in state {State}")]
    public static partial void IgnoredTransition(ILogger logger, string kind, Guid commandId, string state);
}
