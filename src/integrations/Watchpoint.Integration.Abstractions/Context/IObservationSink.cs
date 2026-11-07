using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Abstractions.Context;

/// <summary>
/// Receives what a connector observes (INT §7.1). <c>Report*</c> methods are non-blocking and thread-safe and buffer
/// (rules S1–S6): call <see cref="FlushAsync"/> at least once per loop iteration and before persisting an event cursor.
/// Reports for devices not imported are dropped and counted as unknown.
/// </summary>
public interface IObservationSink
{
    /// <summary>Reports a property value.</summary>
    /// <param name="device">The device.</param>
    /// <param name="propertyPath">The property path, e.g. <c>doorPosition.position</c>.</param>
    /// <param name="value">The value, matching the property type.</param>
    /// <param name="quality">The quality.</param>
    /// <param name="sourceTime">The time in the integrated system, if known.</param>
    /// <param name="unit">The unit of a numeric value, if different from the property's (converted by MOD).</param>
    /// <param name="causedByCommand">The command that caused the change, if known.</param>
    void ReportProperty(DeviceKey device, string propertyPath, WpValue value, Quality quality = Quality.Good,
        DateTimeOffset? sourceTime = null, string? unit = null, Guid? causedByCommand = null);

    /// <summary>Reports an event.</summary>
    /// <param name="device">The device.</param>
    /// <param name="eventPath">The event path, e.g. <c>doorPosition.forced</c>.</param>
    /// <param name="payload">The payload fields.</param>
    /// <param name="sourceTime">The time in the integrated system, if known.</param>
    void ReportEvent(DeviceKey device, string eventPath, IReadOnlyDictionary<string, WpValue>? payload = null,
        DateTimeOffset? sourceTime = null);

    /// <summary>Reports the full health state of a device.</summary>
    /// <param name="device">The device.</param>
    /// <param name="connectivity">The connectivity.</param>
    /// <param name="faults">Active faults, e.g. <c>batteryLow</c>.</param>
    /// <param name="reason">A short technical reason.</param>
    void ReportDeviceHealth(DeviceKey device, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null);

    /// <summary>Reports the full health state of a server.</summary>
    /// <param name="serverNativeId">The server's native id.</param>
    /// <param name="connectivity">The connectivity.</param>
    /// <param name="faults">Active faults.</param>
    /// <param name="reason">A short technical reason.</param>
    void ReportServerHealth(string serverNativeId, Connectivity connectivity, IReadOnlyList<string>? faults = null, string? reason = null);

    /// <summary>Reports a server property value.</summary>
    /// <param name="serverNativeId">The server's native id.</param>
    /// <param name="propertyPath">The property path.</param>
    /// <param name="value">The value.</param>
    /// <param name="quality">The quality.</param>
    /// <param name="sourceTime">The time in the integrated system, if known.</param>
    void ReportServerProperty(string serverNativeId, string propertyPath, WpValue value, Quality quality = Quality.Good,
        DateTimeOffset? sourceTime = null);

    /// <summary>Reports that the inventory changed; the runtime reconciles it with the registry (REG §8).</summary>
    /// <param name="partialOrComplete">The changed part, or the complete inventory.</param>
    void ReportInventoryChanged(InventorySnapshot partialOrComplete);

    /// <summary>Completes when everything reported before the call is durable (in the event log) or rejected.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Counts of what was flushed.</returns>
    ValueTask<SinkFlushResult> FlushAsync(CancellationToken ct);
}

/// <summary>What a flush processed.</summary>
/// <param name="Accepted">Reports written to the event log.</param>
/// <param name="Rejected">Reports rejected (invalid values; logged by the runtime).</param>
/// <param name="UnknownDevices">Reports for devices not imported (dropped).</param>
public sealed record SinkFlushResult(int Accepted, int Rejected, int UnknownDevices);
