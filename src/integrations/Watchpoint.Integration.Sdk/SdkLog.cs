using Microsoft.Extensions.Logging;

namespace Watchpoint.Integration.Sdk;

/// <summary>Log messages for Watchpoint.Integration.Sdk (event id range 5000–5099).</summary>
internal static partial class SdkLog
{
    [LoggerMessage(EventId = 5001, EventName = "UnknownVendorCode", Level = LogLevel.Debug, Message = "EnumMap {MapName}: unrecognised vendor code {VendorCode}, falling back to {Fallback}")]
    public static partial void UnknownVendorCode(ILogger logger, string mapName, string vendorCode, string fallback);

    [LoggerMessage(EventId = 5002, EventName = "ProtocolTrace", Level = LogLevel.Trace, Message = "PROTOCOL {Direction}: {Payload}")]
    public static partial void ProtocolTrace(ILogger logger, string direction, string payload);
}
