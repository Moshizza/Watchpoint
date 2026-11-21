using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;

namespace Watchpoint.State.Contracts;

/// <summary>The current value of one property of a device or server (spec STA §4).</summary>
/// <param name="Path">The property path.</param>
/// <param name="Value">The value, or <see langword="null"/> when the property has never been received.</param>
/// <param name="Quality">The quality of <paramref name="Value"/>.</param>
/// <param name="SourceTime">The time of the value in the integrated system, if known.</param>
/// <param name="ReceivedTime">When this node received the value.</param>
/// <param name="LastSeen">The last time the value was observed (refreshed by a touch even when unchanged).</param>
/// <param name="Sequence">The event log sequence of the record that produced this value.</param>
/// <param name="CausedBy">The command this value is a result of, if any.</param>
public sealed record PropertyState(
    PropertyPath Path,
    WpValue? Value,
    Quality Quality,
    DateTimeOffset? SourceTime,
    DateTimeOffset? ReceivedTime,
    DateTimeOffset? LastSeen,
    long Sequence,
    CommandId? CausedBy);
