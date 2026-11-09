using Microsoft.Extensions.Logging;

namespace Watchpoint.Integration.Simulator;

internal static partial class SimLog
{
    [LoggerMessage(EventId = 5001, EventName = "SimulatorSiteGenerated", Level = LogLevel.Information, Message = "Simulator site generated: profile {Profile}, seed {Seed}, {Devices} devices")]
    public static partial void SimulatorSiteGenerated(ILogger logger, string profile, int seed, int devices);

    [LoggerMessage(EventId = 5002, EventName = "SimulatorConnected", Level = LogLevel.Information, Message = "Simulator connected to {Url}")]
    public static partial void SimulatorConnected(ILogger logger, string url);

    [LoggerMessage(EventId = 5003, EventName = "SimulatorConnectionLost", Level = LogLevel.Warning, Message = "Simulator connection lost: {Reason}")]
    public static partial void SimulatorConnectionLost(ILogger logger, string reason);

    [LoggerMessage(EventId = 5010, EventName = "SimulatorCommandExecuted", Level = LogLevel.Debug, Message = "Command {Command} on {Device} → {Outcome}")]
    public static partial void SimulatorCommandExecuted(ILogger logger, string command, string device, string outcome);
}
