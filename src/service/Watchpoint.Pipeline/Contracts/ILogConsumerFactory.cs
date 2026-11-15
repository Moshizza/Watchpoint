namespace Watchpoint.Pipeline.Contracts;

/// <summary>Creates durable consumers of the event log (spec PIP §5.4), used by HIS, other nodes and federation.</summary>
public interface ILogConsumerFactory
{
    /// <summary>
    /// Returns a durable pull consumer: its position survives restarts, delivery is at-least-once and in stream order.
    /// The consumer is created or updated in JetStream on the first read; a new consumer starts at the beginning of the stream.
    /// </summary>
    /// <param name="name">The durable name, unique per stream: 1 to 64 letters, digits, <c>_</c> or <c>-</c>.</param>
    /// <param name="stream">The stream.</param>
    /// <param name="kindFilter">
    /// The kinds wanted; empty for all. A filter is a kind, or a kind pattern where <c>*</c> stands for one token and a
    /// trailing <c>&gt;</c> for the rest, e.g. <c>alarm.*</c>.
    /// </param>
    /// <param name="options">The consumer options.</param>
    /// <returns>The consumer; only one reader may use a durable name at a time.</returns>
    ILogConsumer CreateDurable(string name, LogStream stream, IReadOnlyList<string> kindFilter, LogConsumerOptions options);
}

/// <summary>A durable consumer of the event log.</summary>
public interface ILogConsumer : IAsyncDisposable
{
    /// <summary>
    /// Reads batches of up to <see cref="LogConsumerOptions.MaxBatch"/> records, in sequence order, as soon as records are
    /// available (an idle consumer polls every <see cref="LogConsumerOptions.MaxWait"/>). A batch must be acknowledged with
    /// <see cref="LogRecordBatch.AckAsync"/> after it was processed: asking for the next batch without acknowledging
    /// yields the same batch again, so later records are never delivered before earlier ones. After
    /// <see cref="LogConsumerOptions.MaxDeliver"/> deliveries an unacknowledged batch is logged and dropped. After a
    /// restart, reading resumes after the last acknowledged batch.
    /// </summary>
    /// <param name="ct">The cancellation token; cancelling ends the enumeration.</param>
    /// <returns>The batches.</returns>
    IAsyncEnumerable<LogRecordBatch> ReadBatchesAsync(CancellationToken ct);
}

/// <summary>A batch of records delivered to a durable consumer.</summary>
/// <param name="Records">The records, in sequence order.</param>
/// <param name="AckAsync">Acknowledges the whole batch; completes when JetStream confirmed. Call it after processing.</param>
public sealed record LogRecordBatch(IReadOnlyList<LogRecord> Records, Func<CancellationToken, ValueTask> AckAsync)
{
    /// <summary>Gets the number of times this batch was delivered, this one included (1 for the first delivery).</summary>
    public int Delivery { get; init; } = 1;

    /// <summary>Gets a value indicating whether the batch is dropped if it is not acknowledged this time.</summary>
    public bool IsLastDelivery { get; init; }
}

/// <summary>Options of a durable consumer.</summary>
/// <param name="MaxBatch">The largest batch (1 to 10,000).</param>
/// <param name="MaxWait">How long an idle consumer waits for records before polling again; 1 s when <see langword="null"/>.</param>
/// <param name="AckWait">How long JetStream waits for an acknowledgement before it redelivers; 60 s when <see langword="null"/>.</param>
/// <param name="MaxDeliver">How many times a batch is delivered before it is dropped (≥ 1).</param>
public sealed record LogConsumerOptions(int MaxBatch = 1000, TimeSpan? MaxWait = null, TimeSpan? AckWait = null, int MaxDeliver = 10);
