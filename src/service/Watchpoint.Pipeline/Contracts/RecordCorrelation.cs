using Watchpoint.Kernel.Ids;

namespace Watchpoint.Pipeline.Contracts;

/// <summary>What a record is correlated with (spec PIP §5.2, <c>corr</c>).</summary>
/// <param name="Command">The command that caused the record, if any.</param>
/// <param name="Alarm">The alarm the record belongs to, if any.</param>
/// <param name="AutomationRun">The automation run that caused the record, if any.</param>
public sealed record RecordCorrelation(CommandId? Command = null, AlarmId? Alarm = null, AutomationRunId? AutomationRun = null)
{
    /// <summary>No correlation.</summary>
    public static RecordCorrelation None { get; } = new();
}
