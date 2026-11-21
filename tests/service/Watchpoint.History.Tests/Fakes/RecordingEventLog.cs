using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.History.Tests.Fakes;

/// <summary>An event log writer that keeps what is appended.</summary>
internal sealed class RecordingEventLog : IEventLogWriter
{
    public List<(LogStream Stream, LogEnvelope Record)> Global { get; } = [];

    public ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<LogEnvelope> records, CancellationToken ct) => throw new NotSupportedException();

    public ValueTask<long> AppendGlobalAsync(LogStream stream, LogEnvelope record, CancellationToken ct)
    {
        Global.Add((stream, record));
        return ValueTask.FromResult((long)Global.Count);
    }
}
