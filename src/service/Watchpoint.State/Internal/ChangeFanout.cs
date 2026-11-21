using System.Collections.Concurrent;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Watchpoint.State.Contracts;

namespace Watchpoint.State.Internal;

/// <summary>
/// Non-blocking fan-out of state changes to registered listeners (spec STA §6.5). A listener's queue is bounded;
/// when full the change is discarded for that listener and it is marked <see cref="IStateChangeListener.Overflowed"/>
/// instead of slowing down or dropping older changes for everyone else — the writer (a partition worker) never
/// waits on a listener.
/// </summary>
internal sealed class ChangeFanout(RecentChangesRing ring, ILogger logger) : IStateChangeFeed
{
    private readonly ConcurrentDictionary<Listener, bool> _listeners = new();

    /// <inheritdoc />
    public IStateChangeListener Subscribe(string name, int capacity)
    {
        var listener = new Listener(name, capacity, Remove);
        _listeners.TryAdd(listener, true);
        return listener;
    }

    /// <inheritdoc />
    public IReadOnlyList<StateChange>? GetChangesSince(long sequence, int max) => ring.GetSince(sequence, max);

    /// <summary>Delivers a change to every registered listener; never blocks.</summary>
    public void Publish(StateChange change)
    {
        foreach (var listener in _listeners.Keys)
        {
            if (listener.Offer(change))
            {
                StateLog.ListenerOverflowed(logger, listener.Name);
            }
        }
    }

    private void Remove(Listener listener) => _listeners.TryRemove(listener, out _);

    private sealed class Listener(string name, int capacity, Action<Listener> onDispose) : IStateChangeListener
    {
        private readonly Channel<StateChange> _channel = Channel.CreateBounded<StateChange>(
            new BoundedChannelOptions(capacity) { FullMode = BoundedChannelFullMode.Wait, SingleReader = false, SingleWriter = false });
        private int _overflowed;

        public string Name { get; } = name;

        public ChannelReader<StateChange> Changes => _channel.Reader;

        public bool Overflowed => Interlocked.Exchange(ref _overflowed, 0) == 1;

        /// <summary>Tries to deliver a change; returns <see langword="true"/> the first time the queue was found full.</summary>
        public bool Offer(StateChange change)
        {
            if (_channel.Writer.TryWrite(change))
            {
                return false;
            }

            return Interlocked.Exchange(ref _overflowed, 1) == 0;
        }

        public void Dispose()
        {
            _channel.Writer.TryComplete();
            onDispose(this);
        }
    }
}
