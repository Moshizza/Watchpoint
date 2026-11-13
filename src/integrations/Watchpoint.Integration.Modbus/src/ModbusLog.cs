using Microsoft.Extensions.Logging;

namespace Watchpoint.Integration.Modbus;

internal static partial class ModbusLog
{
    [LoggerMessage(EventId = 5200, Level = LogLevel.Information, Message = "Connecting to Modbus gateway {Host}:{Port} (Transport={Transport})")]
    public static partial void Connecting(ILogger logger, string host, int port, string transport);

    [LoggerMessage(EventId = 5201, Level = LogLevel.Information, Message = "Connected to Modbus gateway {Host}:{Port}")]
    public static partial void Connected(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 5202, Level = LogLevel.Warning, Message = "Modbus unit {UnitId} timed out or offline: {Reason}")]
    public static partial void UnitOffline(ILogger logger, byte unitId, string reason);

    [LoggerMessage(EventId = 5203, Level = LogLevel.Information, Message = "Command {CommandId} ({Command}) executed for unit {UnitId}")]
    public static partial void CommandExecuted(ILogger logger, string commandId, string command, byte unitId);

    [LoggerMessage(EventId = 5204, Level = LogLevel.Error, Message = "Command {CommandId} ({Command}) failed for unit {UnitId}: {Reason}")]
    public static partial void CommandFailed(ILogger logger, string commandId, string command, byte unitId, string reason, Exception ex);

    [LoggerMessage(EventId = 5205, Level = LogLevel.Warning, Message = "Modbus CRC error detected on received frame from {Host}:{Port}")]
    public static partial void CrcError(ILogger logger, string host, int port);

    [LoggerMessage(EventId = 5206, Level = LogLevel.Debug, Message = "Polled unit {UnitId}: {Points} points read")]
    public static partial void UnitPolled(ILogger logger, byte unitId, int points);
}
