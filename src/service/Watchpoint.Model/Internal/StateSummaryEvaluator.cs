using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;

namespace Watchpoint.Model.Internal;

/// <summary>
/// Evaluates overall state summary conforming to spec MOD §5.4:
/// 1. Compute underlying:
///    1. If health.connectivity is offline -> Offline (cause health.connectivity).
///    2. Else, for each StateOverride in order: if property value (quality Good or Uncertain) is in values -> that state; stop.
///    3. Else take the worst severity among:
///       - enum properties: severity of current code;
///       - boolean properties with BooleanSeverity;
///       - health.faults non-empty -> Fault;
///       - measurements: severity from attribute thresholds (alarmAbove, warningAbove, warningBelow, alarmBelow)
///         with hysteresis (default 0).
///       ignoring values with quality Bad or Stale.
///    4. Map severity to state (Alarm, Fault, Warning, NormalActive, Normal); if no value contributed -> Unknown.
///    5. If health.connectivity is unknown and step 3 found no Good value -> Unknown.
/// 2. Displayed State: Disabled if flags.Disabled; else Maintenance if flags.Maintenance; else underlying.
/// 3. Cause = property that determined state (first in path order on ties).
/// </summary>
internal sealed class StateSummaryEvaluator : IStateSummaryEvaluator
{
    private static readonly PropertyPath _connectivityPath = PropertyPath.FromTrusted("health.connectivity");
    private static readonly PropertyPath _faultsPath = PropertyPath.FromTrusted("health.faults");

    public StateSummary Evaluate(
        DeviceTypeDefinition type,
        IDeviceStateView view,
        DeviceFlags flags,
        IReadOnlyDictionary<string, WpValue> attributes,
        StateSummary? previous)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(view);
        ArgumentNullException.ThrowIfNull(attributes);

        var (underlying, cause, measurementSeverities) = ComputeUnderlying(type, view, attributes, previous);

        OverallState displayed;
        if (flags.Disabled)
        {
            displayed = OverallState.Disabled;
        }
        else if (flags.Maintenance)
        {
            displayed = OverallState.Maintenance;
        }
        else
        {
            displayed = underlying;
        }

        return new StateSummary(displayed, underlying, cause, measurementSeverities);
    }

    private static (OverallState State, PropertyPath? Cause, IReadOnlyDictionary<PropertyPath, Severity> MeasurementSeverities) ComputeUnderlying(
        DeviceTypeDefinition type,
        IDeviceStateView view,
        IReadOnlyDictionary<string, WpValue> attributes,
        StateSummary? previous)
    {
        var hasConnectivity = view.TryGet(_connectivityPath, out var connectivityView);
        var isConnectivityOffline = hasConnectivity
            && connectivityView.Quality is Quality.Good or Quality.Uncertain
            && connectivityView.Value is { Kind: WpValueKind.Enum } connVal
            && string.Equals(connVal.AsEnum(), "offline", StringComparison.Ordinal);

        if (isConnectivityOffline)
        {
            return (OverallState.Offline, _connectivityPath, new Dictionary<PropertyPath, Severity>());
        }

        for (var i = 0; i < type.StateOverrides.Count; i++)
        {
            var rule = type.StateOverrides[i];
            if (view.TryGet(rule.Property, out var propView)
                && propView.Quality is Quality.Good or Quality.Uncertain
                && propView.Value is { } val)
            {
                for (var j = 0; j < rule.Values.Count; j++)
                {
                    if (EqualsValue(val, rule.Values[j]))
                    {
                        return (rule.State, rule.Property, new Dictionary<PropertyPath, Severity>());
                    }
                }
            }
        }

        var worstSeverity = Severity.Unknown;
        PropertyPath? worstCause = null;
        var hasAnyGoodValue = false;
        var measurementSeverities = new Dictionary<PropertyPath, Severity>();

        var sortedProperties = type.Properties.OrderBy(p => p.Key.Value, StringComparer.Ordinal);
        foreach (var (propPath, propDef) in sortedProperties)
        {
            if (!view.TryGet(propPath, out var pView))
            {
                continue;
            }

            if (pView.Quality is Quality.Bad or Quality.Stale || pView.Value is null)
            {
                continue;
            }

            if (pView.Quality == Quality.Good)
            {
                hasAnyGoodValue = true;
            }

            var val = pView.Value.Value;
            var propSeverity = Severity.Unknown;

            if (propDef.ValueType == "enum" && val.Kind == WpValueKind.Enum)
            {
                var code = val.AsEnum();
                var enumDef = propDef.EnumValues.FirstOrDefault(e => string.Equals(e.Code, code, StringComparison.Ordinal));
                if (enumDef is not null)
                {
                    propSeverity = enumDef.Severity;
                }
            }
            else if (propDef.ValueType == "boolean" && val.Kind == WpValueKind.Bool && propDef.BooleanSeverity is not null)
            {
                propSeverity = val.AsBool() ? propDef.BooleanSeverity.True : propDef.BooleanSeverity.False;
            }
            else if (propPath == _faultsPath && val.Kind == WpValueKind.List && val.AsList().Count > 0)
            {
                propSeverity = Severity.Fault;
            }
            else if (propDef.ValueType is "decimal" or "integer" && val.Kind is WpValueKind.Decimal or WpValueKind.Int)
            {
                var num = val.Kind == WpValueKind.Decimal ? val.AsDecimal() : val.AsInt();
                var prevSev = previous?.MeasurementSeverities.TryGetValue(propPath, out var s) == true ? s : Severity.Normal;
                propSeverity = EvaluateMeasurementSeverity(propPath, num, attributes, prevSev);
                measurementSeverities[propPath] = propSeverity;
            }

            if (propSeverity > worstSeverity)
            {
                worstSeverity = propSeverity;
                worstCause = propPath;
            }
        }

        var isConnectivityUnknown = hasConnectivity
            && connectivityView.Value is { Kind: WpValueKind.Enum } connUnknown
            && string.Equals(connUnknown.AsEnum(), "unknown", StringComparison.Ordinal);

        if (isConnectivityUnknown && !hasAnyGoodValue)
        {
            return (OverallState.Unknown, null, measurementSeverities);
        }

        if (worstSeverity == Severity.Unknown)
        {
            return (OverallState.Unknown, null, measurementSeverities);
        }

        var mappedState = worstSeverity switch
        {
            Severity.Alarm => OverallState.Alarm,
            Severity.Fault => OverallState.Fault,
            Severity.Warning => OverallState.Warning,
            Severity.NormalActive => OverallState.NormalActive,
            Severity.Normal => OverallState.Normal,
            _ => OverallState.Unknown,
        };

        return (mappedState, worstCause, measurementSeverities);
    }

    private static Severity EvaluateMeasurementSeverity(
        PropertyPath path,
        decimal value,
        IReadOnlyDictionary<string, WpValue> attributes,
        Severity previousSeverity)
    {
        var alarmAbove = GetAttr(attributes, path, "alarmAbove");
        var warningAbove = GetAttr(attributes, path, "warningAbove");
        var warningBelow = GetAttr(attributes, path, "warningBelow");
        var alarmBelow = GetAttr(attributes, path, "alarmBelow");
        var hysteresis = GetAttr(attributes, path, "hysteresis") ?? 0m;

        if (alarmAbove.HasValue)
        {
            if (previousSeverity == Severity.Alarm)
            {
                if (value >= alarmAbove.Value - hysteresis)
                {
                    return Severity.Alarm;
                }
            }
            else if (value >= alarmAbove.Value)
            {
                return Severity.Alarm;
            }
        }

        if (warningAbove.HasValue)
        {
            if (previousSeverity == Severity.Warning)
            {
                if (value >= warningAbove.Value - hysteresis)
                {
                    return Severity.Warning;
                }
            }
            else if (value >= warningAbove.Value)
            {
                return Severity.Warning;
            }
        }

        if (alarmBelow.HasValue)
        {
            if (previousSeverity == Severity.Alarm)
            {
                if (value <= alarmBelow.Value + hysteresis)
                {
                    return Severity.Alarm;
                }
            }
            else if (value <= alarmBelow.Value)
            {
                return Severity.Alarm;
            }
        }

        if (warningBelow.HasValue)
        {
            if (previousSeverity == Severity.Warning)
            {
                if (value <= warningBelow.Value + hysteresis)
                {
                    return Severity.Warning;
                }
            }
            else if (value <= warningBelow.Value)
            {
                return Severity.Warning;
            }
        }

        return Severity.Normal;
    }

    private static decimal? GetAttr(IReadOnlyDictionary<string, WpValue> attributes, PropertyPath path, string key)
    {
        if (attributes.TryGetValue($"{path.Member.Value}.{key}", out var val) || attributes.TryGetValue(key, out val))
        {
            if (val.Kind == WpValueKind.Decimal)
            {
                return val.AsDecimal();
            }

            if (val.Kind == WpValueKind.Int)
            {
                return val.AsInt();
            }
        }

        return null;
    }

    private static bool EqualsValue(WpValue a, WpValue b)
    {
        if (a.Kind != b.Kind)
        {
            if (a.Kind == WpValueKind.Enum && b.Kind == WpValueKind.String)
            {
                return string.Equals(a.AsEnum(), b.AsString(), StringComparison.Ordinal);
            }

            if (a.Kind == WpValueKind.String && b.Kind == WpValueKind.Enum)
            {
                return string.Equals(a.AsString(), b.AsEnum(), StringComparison.Ordinal);
            }

            if (a.Kind is WpValueKind.Int or WpValueKind.Decimal && b.Kind is WpValueKind.Int or WpValueKind.Decimal)
            {
                var decA = a.Kind == WpValueKind.Decimal ? a.AsDecimal() : a.AsInt();
                var decB = b.Kind == WpValueKind.Decimal ? b.AsDecimal() : b.AsInt();
                return decA == decB;
            }

            return false;
        }

        return a == b;
    }
}
