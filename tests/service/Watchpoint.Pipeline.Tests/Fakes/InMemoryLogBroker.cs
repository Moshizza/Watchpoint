using System.Runtime.CompilerServices;
using Watchpoint.Pipeline.Internal.Log;

namespace Watchpoint.Pipeline.Tests.Fakes;

/// <summary>
/// In-memory stand-in for JetStream (spec PIP §14): streams with sequences, deduplication by message id, ordered reads,
/// durable consumers with an acknowledgement floor, and stream management. Failures are injected by the tests.
/// Everything completes synchronously unless a test holds an acknowledgement or a consumer is idle.
/// </summary>
internal sealed class InMemoryLogBroker(TimeProvider timeProvider) : ILogBroker
{
    private readonly Lock _gate = new();
    private readonly Dictionary<string, Stream> _streams = new(StringComparer.Ordinal);
    private readonly Dictionary<string, StreamSettings> _settings = new(StringComparer.Ordinal);
    private readonly Dictionary<(string Stream, string Name), long> _ackFloors = [];

    public bool IsConnected { get; set; } = true;

    /// <summary>Publishes fail with "unavailable" while this returns true (attempt number per message id, 1-based).</summary>
    public Func<BrokerMessage, int, PublishFault>? PublishFault { get; set; }

    /// <summary>When set, a publish is stored at once but its acknowledgement waits for this task.</summary>
    public Task? AckGate { get; set; }

    /// <summary>The next read breaks off with "unavailable" after this many messages.</summary>
    public int? BreakNextReadAfter { get; set; }

    /// <summary>The storage JetStream can reserve for streams (sum of <see cref="StreamSettings.MaxBytes"/>).</summary>
    public long StorageLimit { get; set; } = long.MaxValue;

    public List<(string MessageId, string Subject)> PublishAttempts { get; } = [];

    public List<string> ManagementCalls { get; } = [];

    public int Opens { get; private set; }

    public IReadOnlyList<StoredMessage> Messages(string stream)
    {
        lock (_gate)
        {
            return _streams.TryGetValue(stream, out var s) ? [.. s.Messages] : [];
        }
    }

    public IReadOnlyDictionary<string, StreamSettings> Streams => _settings;

    /// <summary>Retention: removes the messages up to a sequence.</summary>
    public void Purge(string stream, long upToSequence)
    {
        lock (_gate)
        {
            _streams[stream].Messages.RemoveAll(m => m.Sequence <= upToSequence);
        }
    }

    public void AddStream(StreamSettings settings) => _settings[settings.Name] = settings;

    public async ValueTask<BrokerAck> PublishAsync(BrokerMessage message, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        BrokerAck ack;
        Task? gate;
        lock (_gate)
        {
            PublishAttempts.Add((message.MessageId, message.Subject));
            var attempt = PublishAttempts.Count(a => a.MessageId == message.MessageId);
            var fault = PublishFault?.Invoke(message, attempt) ?? Fakes.PublishFault.None;
            if (fault == Fakes.PublishFault.Lost)
            {
                throw new BrokerUnavailableException("injected: publish lost");
            }

            if (fault == Fakes.PublishFault.Rejected)
            {
                throw new BrokerRejectedException("injected: rejected");
            }

            if (!_streams.TryGetValue(message.Stream, out var stream))
            {
                _streams[message.Stream] = stream = new Stream();
            }

            if (stream.Dedup.TryGetValue(message.MessageId, out var existing))
            {
                ack = new BrokerAck(existing, Duplicate: true);
            }
            else
            {
                var sequence = ++stream.LastSequence;
                var headers = message.Headers.ToDictionary(h => h.Key, h => h.Value, StringComparer.OrdinalIgnoreCase);
                stream.Messages.Add(new StoredMessage(sequence, message.Subject, headers, message.Payload));
                stream.Dedup[message.MessageId] = sequence;
                ack = new BrokerAck(sequence, Duplicate: false);
            }

            if (fault == Fakes.PublishFault.AckLost)
            {
                throw new BrokerUnavailableException("injected: acknowledgement lost");
            }

            gate = AckGate;
        }

        if (gate is not null)
        {
            await gate.WaitAsync(ct);
        }

        return ack;
    }

    public ValueTask<BrokerStreamState> GetStateAsync(string stream, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var s))
            {
                return ValueTask.FromResult(new BrokerStreamState(0, 0, 0));
            }

            var first = s.Messages.Count > 0 ? s.Messages[0].Sequence : s.LastSequence + 1;
            return ValueTask.FromResult(new BrokerStreamState(first, s.LastSequence, s.Messages.Count));
        }
    }

    public async IAsyncEnumerable<BrokerDelivery> ReadAsync(
        string stream, IReadOnlyList<string> filters, long fromSequence, long toSequence, [EnumeratorCancellation] CancellationToken ct)
    {
        await Task.CompletedTask;
        var breakAfter = BreakNextReadAfter;
        BreakNextReadAfter = null;
        var delivered = 0;
        foreach (var message in Matching(stream, filters, fromSequence - 1, int.MaxValue))
        {
            if (message.Sequence > toSequence)
            {
                yield break;
            }

            if (breakAfter == delivered)
            {
                throw new BrokerUnavailableException("injected: read broke off");
            }

            delivered++;
            yield return message;
        }
    }

    public ValueTask<IBrokerConsumer> OpenConsumerAsync(string stream, BrokerConsumerSettings settings, CancellationToken ct)
    {
        Opens++;
        lock (_gate)
        {
            _ackFloors.TryAdd((stream, settings.Name), 0);
        }

        // Opening rewinds to the acknowledgement floor, like the JetStream broker does.
        return ValueTask.FromResult<IBrokerConsumer>(new Consumer(this, stream, settings));
    }

    public ValueTask<StreamSettings?> GetStreamAsync(string name, CancellationToken ct)
    {
        ManagementCalls.Add("get " + name);
        return ValueTask.FromResult(_settings.GetValueOrDefault(name));
    }

    public ValueTask CreateStreamAsync(StreamSettings settings, CancellationToken ct)
    {
        ManagementCalls.Add("create " + settings.Name);
        Reserve(settings);
        return ValueTask.CompletedTask;
    }

    public ValueTask UpdateStreamAsync(StreamSettings settings, CancellationToken ct)
    {
        ManagementCalls.Add("update " + settings.Name);
        Reserve(settings);
        return ValueTask.CompletedTask;
    }

    private void Reserve(StreamSettings settings)
    {
        var others = _settings.Where(s => s.Key != settings.Name).Sum(s => s.Value.MaxBytes);
        if (others + settings.MaxBytes > StorageLimit)
        {
            throw new BrokerStorageException("insufficient storage resources available (10047)");
        }

        _settings[settings.Name] = settings;
    }

    private List<BrokerDelivery> Matching(string stream, IReadOnlyList<string> filters, long afterSequence, int max)
    {
        lock (_gate)
        {
            if (!_streams.TryGetValue(stream, out var s))
            {
                return [];
            }

            return [.. s.Messages
                .Where(m => m.Sequence > afterSequence && filters.Any(f => LogNaming.Matches(f, m.Subject)))
                .Take(max)
                .Select(m => new BrokerDelivery(m.Sequence, m.Subject, m.Headers, m.Payload))];
        }
    }

    internal sealed record StoredMessage(long Sequence, string Subject, IReadOnlyDictionary<string, string> Headers, byte[] Payload);

    private sealed class Stream
    {
        public List<StoredMessage> Messages { get; } = [];

        public Dictionary<string, long> Dedup { get; } = new(StringComparer.Ordinal);

        public long LastSequence { get; set; }
    }

    private sealed class Consumer(InMemoryLogBroker broker, string stream, BrokerConsumerSettings settings) : IBrokerConsumer
    {
        private long _cursor = broker._ackFloors[(stream, settings.Name)];

        public async ValueTask<BrokerFetch> FetchAsync(int maxMessages, TimeSpan maxWait, CancellationToken ct)
        {
            var messages = broker.Matching(stream, settings.Filters, _cursor, maxMessages);
            if (messages.Count == 0)
            {
                await Task.Delay(maxWait, broker.TimeProvider, ct);
                return new BrokerFetch([], _ => ValueTask.CompletedTask);
            }

            _cursor = messages[^1].Sequence;
            var last = _cursor;
            return new BrokerFetch(messages, _ =>
            {
                lock (broker._gate)
                {
                    var key = (stream, settings.Name);
                    broker._ackFloors[key] = Math.Max(broker._ackFloors[key], last);
                }

                return ValueTask.CompletedTask;
            });
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private TimeProvider TimeProvider => timeProvider;
}

internal enum PublishFault
{
    /// <summary>The publish succeeds.</summary>
    None,

    /// <summary>The message never reaches the stream.</summary>
    Lost,

    /// <summary>The message is stored but its acknowledgement is lost.</summary>
    AckLost,

    /// <summary>The broker refuses the message for good.</summary>
    Rejected,
}
