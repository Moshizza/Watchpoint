using System.Runtime.CompilerServices;
using NATS.Client.Core;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using Watchpoint.Pipeline.Internal.Log;

namespace Watchpoint.Pipeline.Internal.Nats;

/// <summary>
/// The event log broker on NATS JetStream (spec PIP §5, ADR-001). Every call carries a cancellation token with a
/// deadline: a JetStream call on a disconnected client would otherwise wait for the connection for ever.
/// </summary>
internal sealed class NatsLogBroker(NatsConnectionProvider connections, PipelineOptions options, TimeProvider timeProvider) : ILogBroker
{
    private const int StreamNotFound = 10059;
    private const int ConsumerNotFound = 10014;
    private const int InsufficientStorage = 10047;
    private const int MessageTooLarge = 10054;
    private const int EmptyReadsBeforeFailure = 5;

    /// <summary>The shortest long poll JetStream accepts is just above one second.</summary>
    private static readonly TimeSpan _minLongPoll = TimeSpan.FromMilliseconds(1100);

    public bool IsConnected => connections.IsConnected;

    public async ValueTask<BrokerAck> PublishAsync(BrokerMessage message, CancellationToken ct)
    {
        var headers = new NatsHeaders(message.Headers.Count);
        foreach (var (name, value) in message.Headers)
        {
            if (!string.Equals(name, LogMessageCodec.MessageIdHeader, StringComparison.Ordinal))
            {
                headers.Add(name, value);
            }
        }

        NatsResult<PubAckResponse> result;
        try
        {
            // One attempt: repeating is the writer's job, with the same message id.
            result = await connections.WriterContext().TryPublishAsync(
                message.Subject,
                message.Payload,
                opts: new NatsJSPubOpts { MsgId = message.MessageId, RetryAttempts = 1 },
                headers: headers,
                cancellationToken: ct).ConfigureAwait(false);
        }
        catch (NatsPayloadTooLargeException ex)
        {
            throw new BrokerRejectedException(ex.Message, tooLarge: true, ex);
        }
        catch (NatsException ex)
        {
            throw new BrokerUnavailableException(ex.Message, ex);
        }

        if (!result.Success)
        {
            throw result.Error switch
            {
                NatsPayloadTooLargeException ex => new BrokerRejectedException(ex.Message, tooLarge: true, ex),
                OperationCanceledException ex => ex,
                var ex => new BrokerUnavailableException(ex.Message, ex),
            };
        }

        var ack = result.Value;
        if (ack.Error is { } error)
        {
            throw error.ErrCode == MessageTooLarge
                ? new BrokerRejectedException(error.Description ?? "message too large", tooLarge: true)
                : new BrokerUnavailableException($"JetStream refused the publish: {error.Description} ({error.ErrCode})");
        }

        if (!string.Equals(ack.Stream, message.Stream, StringComparison.Ordinal))
        {
            throw new BrokerUnavailableException($"Subject {message.Subject} is captured by stream {ack.Stream}, not {message.Stream}.");
        }

        return new BrokerAck(checked((long)ack.Seq), ack.Duplicate);
    }

    public async ValueTask<BrokerStreamState> GetStateAsync(string stream, CancellationToken ct)
    {
        try
        {
            var info = await connections.ReaderContext().GetStreamAsync(stream, cancellationToken: ct).ConfigureAwait(false);
            var state = info.Info.State;
            return new BrokerStreamState(checked((long)state.FirstSeq), checked((long)state.LastSeq), state.Messages);
        }
        catch (NatsException ex)
        {
            throw new BrokerUnavailableException(Describe(ex), ex);
        }
    }

    public async IAsyncEnumerable<BrokerDelivery> ReadAsync(
        string stream, IReadOnlyList<string> filters, long fromSequence, long toSequence, [EnumeratorCancellation] CancellationToken ct)
    {
        INatsJSConsumer consumer;
        try
        {
            using var timeout = new Deadline(options.OperationTimeout, timeProvider, ct);
            consumer = await connections.ReaderContext().CreateOrderedConsumerAsync(
                stream,
                new NatsJSOrderedConsumerOpts
                {
                    FilterSubjects = [.. filters],
                    DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence,
                    OptStartSeq = checked((ulong)fromSequence),
                },
                timeout.Token).ConfigureAwait(false);
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            throw new BrokerUnavailableException(Describe(ex), ex);
        }

        var emptyReads = 0;
        while (true)
        {
            var page = new List<BrokerDelivery>(options.ReadPageSize);
            ulong pending;
            try
            {
                // The ordered consumer starts every request after the last message it delivered; the client detects gaps.
                using var timeout = new Deadline(options.OperationTimeout + options.OperationTimeout, timeProvider, ct);
                await foreach (var message in consumer.FetchNoWaitAsync<byte[]>(
                    new NatsJSFetchOpts { MaxMsgs = options.ReadPageSize }, cancellationToken: timeout.Token).ConfigureAwait(false))
                {
                    page.Add(ToDelivery(message));
                }

                pending = consumer.Info.NumPending;
            }
            catch (Exception ex) when (IsUnavailable(ex, ct))
            {
                throw new BrokerUnavailableException(Describe(ex), ex);
            }

            foreach (var delivery in page)
            {
                if (delivery.Sequence > toSequence)
                {
                    yield break;
                }

                yield return delivery;
            }

            if (page.Count > 0)
            {
                emptyReads = 0;
                continue;
            }

            // An empty answer is the end only when the server says nothing matched when it created the consumer.
            if (pending == 0)
            {
                yield break;
            }

            if (++emptyReads >= EmptyReadsBeforeFailure)
            {
                throw new BrokerUnavailableException($"JetStream returned no messages although {pending} are pending.");
            }
        }
    }

    public async ValueTask<IBrokerConsumer> OpenConsumerAsync(string stream, BrokerConsumerSettings settings, CancellationToken ct)
    {
        var context = connections.ReaderContext();
        var config = new ConsumerConfig(settings.Name)
        {
            DurableName = settings.Name,
            AckPolicy = ConsumerConfigAckPolicy.Explicit,
            AckWait = settings.AckWait,
            DeliverPolicy = ConsumerConfigDeliverPolicy.All,
            FilterSubjects = [.. settings.Filters],
            MaxAckPending = settings.MaxAckPending,

            // Deliveries are counted by the consumer above (LogConsumer): JetStream must never drop a message by itself.
            MaxDeliver = -1,
        };
        try
        {
            using var timeout = new Deadline(options.OperationTimeout + options.OperationTimeout, timeProvider, ct);

            // JetStream refuses to change where an existing consumer started: keep that part as it is.
            if (await FindConsumerAsync(context, stream, settings.Name, timeout.Token).ConfigureAwait(false) is { } existing)
            {
                config.DeliverPolicy = existing.Info.Config.DeliverPolicy;
                config.OptStartSeq = existing.Info.Config.OptStartSeq;
            }

            var consumer = await context.CreateOrUpdateConsumerAsync(stream, config, timeout.Token).ConfigureAwait(false);
            if (consumer.Info.NumAckPending > 0)
            {
                // A previous reader left unacknowledged deliveries: without a rewind JetStream would deliver newer
                // messages first and those ones only after AckWait, out of order.
                consumer = await RewindAsync(context, stream, config, consumer, timeout.Token).ConfigureAwait(false);
            }

            return new NatsBrokerConsumer(consumer, options, timeProvider);
        }
        catch (Exception ex) when (IsUnavailable(ex, ct))
        {
            throw new BrokerUnavailableException(Describe(ex), ex);
        }
    }

    public async ValueTask<StreamSettings?> GetStreamAsync(string name, CancellationToken ct)
    {
        try
        {
            var stream = await connections.ReaderContext().GetStreamAsync(name, cancellationToken: ct).ConfigureAwait(false);
            var config = stream.Info.Config;
            return new StreamSettings
            {
                Name = config.Name ?? name,
                Subjects = [.. config.Subjects ?? []],
                MaxAge = config.MaxAge,
                MaxBytes = config.MaxBytes,
                DuplicateWindow = config.DuplicateWindow,
                MaxMessageSize = config.MaxMsgSize,
                Replicas = config.NumReplicas,
                FileStorage = config.Storage == StreamConfigStorage.File,
                LimitsRetention = config.Retention == StreamConfigRetention.Limits,
                DiscardOld = config.Discard == StreamConfigDiscard.Old,
                AllowDirect = config.AllowDirect,
                DenyDelete = config.DenyDelete,
                DenyPurge = config.DenyPurge,
            };
        }
        catch (NatsJSApiException ex) when (ex.Error.ErrCode == StreamNotFound)
        {
            return null;
        }
        catch (NatsException ex)
        {
            throw new BrokerUnavailableException(Describe(ex), ex);
        }
    }

    public ValueTask CreateStreamAsync(StreamSettings settings, CancellationToken ct) =>
        ManageAsync(async context => await context.CreateStreamAsync(ToConfig(settings), ct).ConfigureAwait(false));

    public ValueTask UpdateStreamAsync(StreamSettings settings, CancellationToken ct) =>
        ManageAsync(async context => await context.UpdateStreamAsync(ToConfig(settings), ct).ConfigureAwait(false));

    internal static StreamConfig ToConfig(StreamSettings settings) => new(settings.Name, [.. settings.Subjects])
    {
        Storage = settings.FileStorage ? StreamConfigStorage.File : StreamConfigStorage.Memory,
        Retention = settings.LimitsRetention ? StreamConfigRetention.Limits : StreamConfigRetention.Workqueue,
        Discard = settings.DiscardOld ? StreamConfigDiscard.Old : StreamConfigDiscard.New,
        MaxAge = settings.MaxAge,
        MaxBytes = settings.MaxBytes,
        DuplicateWindow = settings.DuplicateWindow,
        MaxMsgSize = settings.MaxMessageSize,
        NumReplicas = settings.Replicas,
        AllowDirect = settings.AllowDirect,
        DenyDelete = settings.DenyDelete,
        DenyPurge = settings.DenyPurge,
    };

    private static BrokerDelivery ToDelivery(INatsJSMsg<byte[]> message)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (message.Headers is { } source)
        {
            foreach (var (name, values) in source)
            {
                headers[name] = values.ToString();
            }
        }

        var sequence = message.Metadata?.Sequence.Stream ?? throw new BrokerUnavailableException("A JetStream message has no metadata.");
        return new BrokerDelivery(checked((long)sequence), message.Subject, headers, message.Data ?? []);
    }

    private static bool IsUnavailable(Exception ex, CancellationToken ct) =>
        ex is NatsException or TimeoutException || (ex is OperationCanceledException && !ct.IsCancellationRequested);

    private static string Describe(Exception ex) => ex switch
    {
        NatsJSApiException api => $"{api.Error.Description} ({api.Error.ErrCode})",
        OperationCanceledException => "JetStream did not answer in time",
        _ => ex.Message,
    };

    private static async ValueTask<INatsJSConsumer?> FindConsumerAsync(NatsJSContext context, string stream, string name, CancellationToken ct)
    {
        try
        {
            return await context.GetConsumerAsync(stream, name, ct).ConfigureAwait(false);
        }
        catch (NatsJSApiException ex) when (ex.Error.ErrCode == ConsumerNotFound)
        {
            return null;
        }
    }

    private async ValueTask<INatsJSConsumer> RewindAsync(
        NatsJSContext context, string stream, ConsumerConfig config, INatsJSConsumer consumer, CancellationToken ct)
    {
        if (options.UseConsumerReset)
        {
            // Sequence 0: back to the acknowledgement floor.
            await context.ResetConsumerAsync(stream, config.DurableName!, 0, ct).ConfigureAwait(false);
            return await context.GetConsumerAsync(stream, config.DurableName!, ct).ConfigureAwait(false);
        }

        // Servers without the reset API: re-create the consumer right after its acknowledgement floor.
        var floor = consumer.Info.AckFloor.StreamSeq;
        await context.DeleteConsumerAsync(stream, config.DurableName!, ct).ConfigureAwait(false);
        config.DeliverPolicy = ConsumerConfigDeliverPolicy.ByStartSequence;
        config.OptStartSeq = floor + 1;
        return await context.CreateOrUpdateConsumerAsync(stream, config, ct).ConfigureAwait(false);
    }

    private async ValueTask ManageAsync(Func<NatsJSContext, Task> operation)
    {
        try
        {
            await operation(connections.ReaderContext()).ConfigureAwait(false);
        }
        catch (NatsJSApiException ex) when (ex.Error.ErrCode == InsufficientStorage)
        {
            throw new BrokerStorageException(Describe(ex), ex);
        }
        catch (NatsJSApiException ex)
        {
            throw new BrokerRejectedException(Describe(ex), innerException: ex);
        }
        catch (NatsException ex)
        {
            throw new BrokerUnavailableException(Describe(ex), ex);
        }
    }

    /// <summary>A durable pull consumer.</summary>
    private sealed class NatsBrokerConsumer(INatsJSConsumer consumer, PipelineOptions options, TimeProvider timeProvider) : IBrokerConsumer
    {
        public async ValueTask<BrokerFetch> FetchAsync(int maxMessages, TimeSpan maxWait, CancellationToken ct)
        {
            var messages = new List<INatsJSMsg<byte[]>>();
            try
            {
                // A request is always read to its end: abandoning it would lose messages the server already sent.
                await NoWaitAsync(messages, maxMessages, ct).ConfigureAwait(false);
                if (messages.Count == 0)
                {
                    var poll = maxWait > _minLongPoll ? maxWait : _minLongPoll;
                    using (var timeout = new Deadline(poll + options.OperationTimeout, timeProvider, ct))
                    {
                        await foreach (var message in consumer.FetchAsync<byte[]>(
                            new NatsJSFetchOpts { MaxMsgs = 1, Expires = poll }, cancellationToken: timeout.Token).ConfigureAwait(false))
                        {
                            messages.Add(message);
                        }
                    }

                    if (messages.Count == 0)
                    {
                        // JetStream answers a request for a deleted consumer with silence: make sure it still exists.
                        using var timeout = new Deadline(options.OperationTimeout, timeProvider, ct);
                        await consumer.RefreshAsync(timeout.Token).ConfigureAwait(false);
                    }
                    else if (maxMessages > 1)
                    {
                        await NoWaitAsync(messages, maxMessages - 1, ct).ConfigureAwait(false);
                    }
                }
            }
            catch (NatsJSApiException ex) when (ex.Error.ErrCode is ConsumerNotFound or StreamNotFound)
            {
                throw new BrokerUnavailableException($"The consumer is gone: {Describe(ex)}", ex);
            }
            catch (Exception ex) when (IsUnavailable(ex, ct))
            {
                throw new BrokerUnavailableException(Describe(ex), ex);
            }

            messages.Sort(static (a, b) => a.Metadata!.Value.Sequence.Stream.CompareTo(b.Metadata!.Value.Sequence.Stream));
            var deliveries = new List<BrokerDelivery>(messages.Count);
            foreach (var message in messages)
            {
                deliveries.Add(ToDelivery(message));
            }

            return new BrokerFetch(deliveries, token => AckAsync(messages, token));
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;

        private async ValueTask NoWaitAsync(List<INatsJSMsg<byte[]>> messages, int max, CancellationToken ct)
        {
            using var timeout = new Deadline(options.OperationTimeout + options.OperationTimeout, timeProvider, ct);
            await foreach (var message in consumer.FetchNoWaitAsync<byte[]>(
                new NatsJSFetchOpts { MaxMsgs = max }, cancellationToken: timeout.Token).ConfigureAwait(false))
            {
                messages.Add(message);
            }
        }

        private async ValueTask AckAsync(List<INatsJSMsg<byte[]>> messages, CancellationToken ct)
        {
            try
            {
                // JetStream handles the acknowledgements of a consumer in the order it receives them, so the
                // confirmation of the last one covers the others.
                using var timeout = new Deadline(options.OperationTimeout, timeProvider, ct);
                for (var i = 0; i < messages.Count - 1; i++)
                {
                    await messages[i].AckAsync(cancellationToken: timeout.Token).ConfigureAwait(false);
                }

                if (messages.Count > 0)
                {
                    await messages[^1].AckAsync(new AckOpts { DoubleAck = true }, timeout.Token).ConfigureAwait(false);
                }
            }
            catch (Exception ex) when (IsUnavailable(ex, ct))
            {
                throw new BrokerUnavailableException(Describe(ex), ex);
            }
        }
    }
}
