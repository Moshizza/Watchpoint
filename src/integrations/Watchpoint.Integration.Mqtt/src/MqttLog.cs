using Microsoft.Extensions.Logging;

namespace Watchpoint.Integration.Mqtt;

internal static partial class MqttLog
{
    [LoggerMessage(EventId = 5300, Level = LogLevel.Information, Message = "Connecting to MQTT broker at {Host}:{Port} (protocol {Protocol})")]
    public static partial void MqttConnecting(ILogger logger, string host, int port, string protocol);

    [LoggerMessage(EventId = 5301, Level = LogLevel.Information, Message = "Connected to MQTT broker at {Host}:{Port} (protocol {Protocol}, sessionPresent={SessionPresent})")]
    public static partial void MqttConnected(ILogger logger, string host, int port, string protocol, bool sessionPresent);

    [LoggerMessage(EventId = 5302, Level = LogLevel.Warning, Message = "Disconnected from MQTT broker: {Reason}")]
    public static partial void MqttDisconnected(ILogger logger, string reason);

    [LoggerMessage(EventId = 5303, Level = LogLevel.Debug, Message = "Subscribed to MQTT topics: {Topics}")]
    public static partial void MqttSubscribed(ILogger logger, string topics);

    [LoggerMessage(EventId = 5304, Level = LogLevel.Warning, Message = "Rate limit exceeded for message on topic {Topic}; coalescing or dropping")]
    public static partial void MqttRateLimitExceeded(ILogger logger, string topic);

    [LoggerMessage(EventId = 5305, Level = LogLevel.Warning, Message = "Incoming MQTT message dropped due to event flood on topic {Topic}")]
    public static partial void MqttEventFloodDropped(ILogger logger, string topic);

    [LoggerMessage(EventId = 5306, Level = LogLevel.Warning, Message = "Failed to decode payload on topic {Topic}: {Error}")]
    public static partial void MqttPayloadDecodeFailed(ILogger logger, string topic, string error);

    [LoggerMessage(EventId = 5307, Level = LogLevel.Information, Message = "Discovered new MQTT device {NativeId} of type {DeviceType}")]
    public static partial void MqttDeviceDiscovered(ILogger logger, string nativeId, string deviceType);

    [LoggerMessage(EventId = 5308, Level = LogLevel.Warning, Message = "Device {NativeId} exceeded max devices limit; discarded")]
    public static partial void MqttMaxDevicesExceeded(ILogger logger, string nativeId);

    [LoggerMessage(EventId = 5309, Level = LogLevel.Warning, Message = "Stale timeout reached for device {NativeId}")]
    public static partial void MqttDeviceStale(ILogger logger, string nativeId);
}
