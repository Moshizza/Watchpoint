using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Registry.Contracts;

namespace Watchpoint.State.Contracts;

/// <summary>A query over the live state of devices (spec STA §5, §6.6).</summary>
/// <param name="Devices">The registry filters (server, location, type, tags, text…).</param>
/// <param name="States">Restricts the result to these overall states, or <see langword="null"/> for any state.</param>
/// <param name="Where">A property value condition, or <see langword="null"/>.</param>
public sealed record StateQuery(DeviceQuery Devices, IReadOnlyList<OverallState>? States = null, PropertyCondition? Where = null);

/// <summary>A property equality condition of a <see cref="StateQuery"/>, e.g. <c>doorPosition.position == forced</c>.</summary>
/// <param name="Path">The property path.</param>
/// <param name="EqualsValue">The value the property must equal.</param>
public sealed record PropertyCondition(PropertyPath Path, WpValue EqualsValue);
