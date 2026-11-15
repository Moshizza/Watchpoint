namespace Watchpoint.Pipeline.Contracts;

/// <summary>
/// Replays the event log (spec PIP §5.4). A replay yields the records that existed when it started, in sequence order,
/// without gaps or duplicates, and resumes by itself after a connection loss. Records that cannot be decoded are
/// logged and skipped; records of unknown kinds are yielded (see <see cref="ILogRecordTypeRegistry.CanRead"/>).
/// </summary>
public interface IEventLogReader
{
    /// <summary>Replays one partition from a sequence (inclusive) until the current end.</summary>
    /// <param name="partition">The partition, 0 to 63.</param>
    /// <param name="fromSequence">The first sequence wanted (values below 1 mean the beginning).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The records of the partition in sequence order.</returns>
    IAsyncEnumerable<LogRecord> ReadPartitionAsync(int partition, long fromSequence, CancellationToken ct);

    /// <summary>Replays several partitions in global sequence order with one consumer (recovery, §7.4).</summary>
    /// <param name="partitions">The partitions.</param>
    /// <param name="fromSequence">The first sequence wanted (values below 1 mean the beginning).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The records of these partitions in sequence order.</returns>
    IAsyncEnumerable<LogRecord> ReadPartitionsAsync(IReadOnlySet<int> partitions, long fromSequence, CancellationToken ct);

    /// <summary>Replays a global stream from a sequence (inclusive) until the current end.</summary>
    /// <param name="stream"><see cref="LogStream.Cfg"/> or <see cref="LogStream.Sys"/>.</param>
    /// <param name="fromSequence">The first sequence wanted (values below 1 mean the beginning).</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The records in sequence order.</returns>
    IAsyncEnumerable<LogRecord> ReadGlobalAsync(LogStream stream, long fromSequence, CancellationToken ct);

    /// <summary>Returns the sequence of the last record of <see cref="LogStream.Log"/> (0 when it never held a record).</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The last sequence.</returns>
    ValueTask<long> GetLastSequenceAsync(CancellationToken ct);

    /// <summary>Returns the first sequence of <see cref="LogStream.Log"/> still available after retention (last + 1 when empty).</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The first available sequence.</returns>
    ValueTask<long> GetFirstAvailableSequenceAsync(CancellationToken ct);
}
