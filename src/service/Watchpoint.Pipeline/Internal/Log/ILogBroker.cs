namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>
/// The message broker under the event log: JetStream in production (<c>NatsLogBroker</c>), an in-memory fake in unit
/// tests. One call is one attempt; retries, ordering and decoding are done by the writer, reader and consumer above.
/// </summary>
internal interface ILogBroker
{
    /// <summary>Gets a value indicating whether the broker is connected.</summary>
    bool IsConnected { get; }

    /// <summary>Publishes one message and waits for its acknowledgement.</summary>
    /// <exception cref="BrokerUnavailableException">No acknowledgement: the message may or may not be stored.</exception>
    /// <exception cref="BrokerRejectedException">The broker refused the message for good.</exception>
    ValueTask<BrokerAck> PublishAsync(BrokerMessage message, CancellationToken ct);

    /// <summary>Returns the first and last sequence of a stream.</summary>
    /// <exception cref="BrokerUnavailableException">The broker did not answer.</exception>
    ValueTask<BrokerStreamState> GetStateAsync(string stream, CancellationToken ct);

    /// <summary>
    /// Reads the messages matching <paramref name="filters"/> with sequences <paramref name="fromSequence"/> to
    /// <paramref name="toSequence"/> (inclusive), in sequence order, then completes.
    /// </summary>
    /// <exception cref="BrokerUnavailableException">The read broke off; the caller resumes after the last message it got.</exception>
    IAsyncEnumerable<BrokerDelivery> ReadAsync(
        string stream, IReadOnlyList<string> filters, long fromSequence, long toSequence, CancellationToken ct);

    /// <summary>
    /// Creates or updates a durable consumer and rewinds it to its last acknowledged message, so that messages delivered
    /// to an earlier reader but never acknowledged come first again.
    /// </summary>
    /// <exception cref="BrokerUnavailableException">The broker did not answer.</exception>
    ValueTask<IBrokerConsumer> OpenConsumerAsync(string stream, BrokerConsumerSettings settings, CancellationToken ct);

    /// <summary>Returns the settings of a stream, or <see langword="null"/> when it does not exist.</summary>
    /// <exception cref="BrokerUnavailableException">The broker did not answer.</exception>
    ValueTask<StreamSettings?> GetStreamAsync(string name, CancellationToken ct);

    /// <summary>Creates a stream.</summary>
    /// <exception cref="BrokerUnavailableException">The broker did not answer.</exception>
    /// <exception cref="BrokerStorageException">The broker has less storage than <see cref="StreamSettings.MaxBytes"/>.</exception>
    /// <exception cref="BrokerRejectedException">The broker refused the settings.</exception>
    ValueTask CreateStreamAsync(StreamSettings settings, CancellationToken ct);

    /// <summary>Updates a stream.</summary>
    /// <exception cref="BrokerUnavailableException">The broker did not answer.</exception>
    /// <exception cref="BrokerStorageException">The broker has less storage than <see cref="StreamSettings.MaxBytes"/>.</exception>
    /// <exception cref="BrokerRejectedException">The broker refused the settings.</exception>
    ValueTask UpdateStreamAsync(StreamSettings settings, CancellationToken ct);
}

/// <summary>A durable consumer of the broker.</summary>
internal interface IBrokerConsumer : IAsyncDisposable
{
    /// <summary>
    /// Returns up to <paramref name="maxMessages"/> messages in sequence order as soon as some are available, or an empty
    /// fetch after about <paramref name="maxWait"/>.
    /// </summary>
    /// <exception cref="BrokerUnavailableException">The broker did not answer or the consumer is gone; open it again.</exception>
    ValueTask<BrokerFetch> FetchAsync(int maxMessages, TimeSpan maxWait, CancellationToken ct);
}

/// <summary>A message to publish.</summary>
internal sealed record BrokerMessage(
    string Stream, string Subject, string MessageId, IReadOnlyList<KeyValuePair<string, string>> Headers, byte[] Payload);

/// <summary>The acknowledgement of a publish.</summary>
/// <param name="Sequence">The stream sequence of the message.</param>
/// <param name="Duplicate">Whether the message id was already stored (the sequence is the one of the stored message).</param>
internal readonly record struct BrokerAck(long Sequence, bool Duplicate);

/// <summary>The state of a stream.</summary>
internal readonly record struct BrokerStreamState(long FirstSequence, long LastSequence, long Messages);

/// <summary>A message read from a stream.</summary>
internal sealed record BrokerDelivery(long Sequence, string Subject, IReadOnlyDictionary<string, string> Headers, ReadOnlyMemory<byte> Payload);

/// <summary>Messages delivered to a durable consumer.</summary>
/// <param name="Messages">The messages in sequence order.</param>
/// <param name="AckAsync">Acknowledges all of them; completes when the broker confirmed. Throws <see cref="BrokerUnavailableException"/>.</param>
internal sealed record BrokerFetch(IReadOnlyList<BrokerDelivery> Messages, Func<CancellationToken, ValueTask> AckAsync);

/// <summary>Settings of a durable consumer.</summary>
internal sealed record BrokerConsumerSettings(string Name, IReadOnlyList<string> Filters, TimeSpan AckWait, int MaxAckPending);

/// <summary>The settings of a stream the event log cares about (spec PIP §5.1).</summary>
internal sealed record StreamSettings
{
    public required string Name { get; init; }

    public required IReadOnlyList<string> Subjects { get; init; }

    public required TimeSpan MaxAge { get; init; }

    public required long MaxBytes { get; init; }

    public required TimeSpan DuplicateWindow { get; init; }

    public required int MaxMessageSize { get; init; }

    public required int Replicas { get; init; }

    public bool FileStorage { get; init; } = true;

    public bool LimitsRetention { get; init; } = true;

    public bool DiscardOld { get; init; } = true;

    public bool AllowDirect { get; init; } = true;

    public bool DenyDelete { get; init; } = true;

    public bool DenyPurge { get; init; } = true;
}

/// <summary>The broker did not answer in time or is not reachable; the operation can be repeated.</summary>
internal sealed class BrokerUnavailableException(string message, Exception? innerException = null) : Exception(message, innerException);

/// <summary>The broker refused the request; repeating it does not help.</summary>
internal sealed class BrokerRejectedException(string message, bool tooLarge = false, Exception? innerException = null)
    : Exception(message, innerException)
{
    public bool TooLarge { get; } = tooLarge;
}

/// <summary>The broker cannot reserve the storage a stream asks for.</summary>
internal sealed class BrokerStorageException(string message, Exception? innerException = null) : Exception(message, innerException);
