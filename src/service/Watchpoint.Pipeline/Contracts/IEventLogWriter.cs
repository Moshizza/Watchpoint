namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// Appends records to the event log (spec PIP §5.4). Guarantees:
/// <list type="bullet">
/// <item>a call completes only when JetStream acknowledged every record (durable);</item>
/// <item>records of one partition are stored in the order they are given, also when a publish had to be repeated: the
/// next record of a partition is sent only after the previous one was acknowledged;</item>
/// <item>a record is stored once: a repeated publish carries the same <c>Nats-Msg-Id</c> (the record id) and JetStream
/// drops duplicates for 2 minutes. A caller repeating a failed call must use the same record ids and do so within
/// that window;</item>
/// <item>nothing is buffered: while the log is unreachable a call waits, at most <c>Log:AppendTimeout</c>, then fails.</item>
/// </list>
/// </summary>
public interface IEventLogWriter
{
    /// <summary>Appends device-bound records to <see cref="LogStream.Log"/>.</summary>
    /// <param name="records">The records; all are validated before the first one is sent.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The stream sequence of every record, in input order.</returns>
    /// <exception cref="PipelineException">
    /// <c>pipeline.record.invalid</c> or <c>pipeline.record.too_large</c>: nothing was appended.
    /// <c>pipeline.log.unavailable</c>: for every partition a prefix of its records is stored; repeat the call with the same records.
    /// </exception>
    ValueTask<IReadOnlyList<long>> AppendAsync(IReadOnlyList<LogEnvelope> records, CancellationToken ct);

    /// <summary>Appends one record to a global stream; records of one stream are stored in call order.</summary>
    /// <param name="stream"><see cref="LogStream.Cfg"/> or <see cref="LogStream.Sys"/>.</param>
    /// <param name="record">The record.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The stream sequence of the record.</returns>
    /// <exception cref="PipelineException">As for <see cref="AppendAsync"/>.</exception>
    ValueTask<long> AppendGlobalAsync(LogStream stream, LogEnvelope record, CancellationToken ct);
}
