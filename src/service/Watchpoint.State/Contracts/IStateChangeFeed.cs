using System.Threading.Channels;

namespace Watchpoint.State.Contracts;

/// <summary>
/// Publishes state changes for the live API (spec STA §5, §6.5). Fan-out is non-blocking: a listener that cannot keep
/// up overflows instead of slowing down the writer.
/// </summary>
public interface IStateChangeFeed
{
    /// <summary>Registers a listener with a bounded queue.</summary>
    /// <param name="name">A name for logs and metrics.</param>
    /// <param name="capacity">The queue capacity.</param>
    /// <returns>The listener; dispose it to unregister.</returns>
    IStateChangeListener Subscribe(string name, int capacity);

    /// <summary>Gets the changes after a sequence, for a listener to resynchronise.</summary>
    /// <param name="sequence">The last sequence the caller already has.</param>
    /// <param name="max">The maximum number of changes to return.</param>
    /// <returns>The changes with sequence greater than <paramref name="sequence"/>, or <see langword="null"/> when the ring no longer holds that sequence.</returns>
    IReadOnlyList<StateChange>? GetChangesSince(long sequence, int max);
}

/// <summary>A registered listener of an <see cref="IStateChangeFeed"/>.</summary>
public interface IStateChangeListener : IDisposable
{
    /// <summary>Gets the channel of changes delivered to this listener.</summary>
    ChannelReader<StateChange> Changes { get; }

    /// <summary>
    /// Gets a value indicating whether the listener's queue overflowed: changes were discarded because the listener did
    /// not drain fast enough. Reset the next time this is read after an overflow; the listener must resynchronise
    /// (<see cref="IStateChangeFeed.GetChangesSince"/> or a fresh query) after observing <see langword="true"/>.
    /// </summary>
    bool Overflowed { get; }
}
