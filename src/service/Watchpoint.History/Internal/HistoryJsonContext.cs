using System.Text.Json.Serialization;
using Watchpoint.History.Contracts;
using Watchpoint.Kernel.Values;

namespace Watchpoint.History.Internal;

/// <summary>Source-generated JSON metadata of the record bodies history reads and of the values it writes.</summary>
[JsonSerializable(typeof(SysEventBody))]
[JsonSerializable(typeof(OperatorNoteBody))]
[JsonSerializable(typeof(AlarmTransitionBody))]
[JsonSerializable(typeof(Dictionary<string, WpValue>))]
[JsonSerializable(typeof(WpValue))]
[JsonSerializable(typeof(HistoryAlarmTimelineBody))]
[JsonSerializable(typeof(HistoryCorrelation))]
[JsonSerializable(typeof(HistoryPolicyDocument))]
internal sealed partial class HistoryJsonContext : JsonSerializerContext;

/// <summary>The JSON stored in the <c>body</c> column of an alarm timeline entry.</summary>
/// <param name="State">The state after the transition.</param>
/// <param name="Priority">The priority after the transition.</param>
/// <param name="Count">The occurrence count after the transition.</param>
/// <param name="Detail">The kind-specific detail.</param>
internal sealed record HistoryAlarmTimelineBody(AlarmHistoryState State, int Priority, int Count, System.Text.Json.JsonElement? Detail);

/// <summary>The JSON stored in the <c>correlation</c> columns.</summary>
/// <param name="Command">The command.</param>
/// <param name="Alarm">The alarm.</param>
/// <param name="AutomationRun">The automation run.</param>
internal sealed record HistoryCorrelation(Guid? Command, Guid? Alarm, Guid? AutomationRun);

/// <summary>A policy document of <c>wp_history.policy_overrides</c>.</summary>
/// <param name="Mode">The mode.</param>
/// <param name="Type"><c>absolute</c> or <c>percent</c>.</param>
/// <param name="Threshold">The deadband threshold.</param>
/// <param name="Interval">The sampling interval (ISO 8601 duration).</param>
internal sealed record HistoryPolicyDocument(string? Mode, string? Type, double? Threshold, TimeSpan? Interval);
