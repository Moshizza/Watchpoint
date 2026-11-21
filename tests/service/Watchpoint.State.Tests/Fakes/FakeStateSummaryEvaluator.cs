using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;

namespace Watchpoint.State.Tests.Fakes;

/// <summary>
/// A test double of MOD's <see cref="IStateSummaryEvaluator"/> (internal to <c>Watchpoint.Model</c>, so not directly
/// usable from this module's tests): the documented contract of <see cref="IStateSummaryEvaluator.Evaluate"/>,
/// enough to drive the state module's own behaviour (recompute, aggregates, change feed) — not a conformance test of
/// MOD's own override and hysteresis rules, which belong to <c>Watchpoint.Model.Tests</c>.
/// </summary>
internal sealed class FakeStateSummaryEvaluator : IStateSummaryEvaluator
{
    private static readonly Dictionary<PropertyPath, Severity> _emptySeverities = [];

    public StateSummary Evaluate(
        DeviceTypeDefinition type,
        IDeviceStateView view,
        DeviceFlags flags,
        IReadOnlyDictionary<string, WpValue> attributes,
        StateSummary? previous)
    {
        ArgumentNullException.ThrowIfNull(type);
        ArgumentNullException.ThrowIfNull(view);

        // Nothing observed yet (no default seeded a value either): state cannot be determined.
        var anyKnown = type.Properties.Keys.Any(path => view.TryGet(path, out var v) && v.Value is not null);
        OverallState underlying;
        PropertyPath? cause = null;
        if (!anyKnown)
        {
            underlying = OverallState.Unknown;
        }
        else
        {
            underlying = OverallState.Normal;
            foreach (var @override in type.StateOverrides)
            {
                if (view.TryGet(@override.Property, out var value) && value.Value is { } v && @override.Values.Any(candidate => candidate.Equals(v)))
                {
                    underlying = @override.State;
                    cause = @override.Property;
                    break;
                }
            }
        }

        var state = flags.Disabled ? OverallState.Disabled : flags.Maintenance ? OverallState.Maintenance : underlying;
        return new StateSummary(state, underlying, cause, _emptySeverities);
    }
}
