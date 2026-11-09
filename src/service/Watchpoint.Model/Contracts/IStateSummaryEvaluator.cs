using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Model.Contracts;

/// <summary>Evaluates the overall state summary of a device (spec MOD §4.4).</summary>
public interface IStateSummaryEvaluator
{
    /// <summary>Evaluates current device state given current properties, flags, attributes and previous summary.</summary>
    /// <param name="type">The device type definition.</param>
    /// <param name="view">The view over current property values and quality.</param>
    /// <param name="flags">Device flags (disabled, maintenance).</param>
    /// <param name="attributes">Device attribute values.</param>
    /// <param name="previous">Previous state summary, if any (used for threshold hysteresis).</param>
    /// <returns>The computed state summary.</returns>
    StateSummary Evaluate(
        DeviceTypeDefinition type,
        IDeviceStateView view,
        DeviceFlags flags,
        IReadOnlyDictionary<string, WpValue> attributes,
        StateSummary? previous);
}

/// <summary>View over current device property values with quality indicators.</summary>
public interface IDeviceStateView
{
    /// <summary>Attempts to get the current value and quality for the given property path.</summary>
    /// <param name="path">The property path.</param>
    /// <param name="value">The retrieved property value view.</param>
    /// <returns><see langword="true"/> if found; otherwise <see langword="false"/>.</returns>
    bool TryGet(PropertyPath path, out PropertyValueView value);
}

/// <summary>A property value together with its quality indicator.</summary>
/// <param name="Value">The property value, or <see langword="null"/>.</param>
/// <param name="Quality">The value quality indicator.</param>
public readonly record struct PropertyValueView(WpValue? Value, Quality Quality);

/// <summary>Administrative device flags affecting overall state.</summary>
/// <param name="Disabled">Whether the device is administratively disabled.</param>
/// <param name="Maintenance">Whether the device is currently in maintenance mode.</param>
public readonly record struct DeviceFlags(bool Disabled, bool Maintenance);

/// <summary>The computed state summary of a device (spec MOD §4.4).</summary>
/// <param name="State">The displayed overall state (takes flags into account).</param>
/// <param name="UnderlyingState">The underlying operational state computed from properties and overrides.</param>
/// <param name="Cause">The property path that determined the state, or <see langword="null"/>.</param>
/// <param name="MeasurementSeverities">Severities evaluated for measurement properties with hysteresis.</param>
public sealed record StateSummary(
    OverallState State,
    OverallState UnderlyingState,
    PropertyPath? Cause,
    IReadOnlyDictionary<PropertyPath, Severity> MeasurementSeverities);
