using Watchpoint.Model.Contracts;

namespace Watchpoint.State.Contracts;

/// <summary>The last occurrence of one event path of a device (spec STA §4).</summary>
/// <param name="Path">The event path.</param>
/// <param name="Time">When the event occurred (received time).</param>
/// <param name="Severity">The severity of the event.</param>
/// <param name="Sequence">The event log sequence of the record.</param>
public sealed record LastEvent(EventPath Path, DateTimeOffset Time, Severity Severity, long Sequence);
