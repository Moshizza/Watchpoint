using System.Collections.Concurrent;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Integrations.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Commands.Tests;

/// <summary>
/// The connector channel of the dispatch tests: every call is recorded and waits until the test completes it
/// (<see cref="Complete"/>), unless <see cref="Handler"/> answers it. Cancellation at the deadline is honoured unless
/// <see cref="IgnoreCancellation"/> is set (a connector that never returns).
/// </summary>
internal sealed class FakeChannel : IConnectionCommandChannel
{
    private readonly ConcurrentDictionary<Guid, TaskCompletionSource<ConnectorCommandResult>> _pending = new();
    private readonly List<ConnectorCommand> _calls = [];
    private int _inFlight;

    /// <summary>Answers calls directly instead of waiting for <see cref="Complete"/>.</summary>
    public Func<ConnectorCommand, ConnectorCommandResult>? Handler { get; set; }

    /// <summary>Whether a waiting call ignores its cancellation token.</summary>
    public bool IgnoreCancellation { get; set; }

    /// <summary>The most calls in flight at the same time.</summary>
    public int MaxInFlight { get; private set; }

    public IReadOnlyList<ConnectorCommand> Calls
    {
        get
        {
            lock (_calls)
            {
                return [.. _calls];
            }
        }
    }

    /// <summary>The calls waiting for <see cref="Complete"/>.</summary>
    public int Waiting => _pending.Count;

    public async ValueTask<ConnectorCommandResult> ExecuteAsync(ConnectionId connection, ConnectorCommand command, CancellationToken ct)
    {
        lock (_calls)
        {
            _calls.Add(command);
            MaxInFlight = Math.Max(MaxInFlight, ++_inFlight);
        }

        try
        {
            if (Handler is { } handler)
            {
                return handler(command);
            }

            var waiter = _pending.GetOrAdd(command.CommandId, _ => new TaskCompletionSource<ConnectorCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously));
            return IgnoreCancellation ? await waiter.Task.ConfigureAwait(false) : await waiter.Task.WaitAsync(ct).ConfigureAwait(false);
        }
        finally
        {
            lock (_calls)
            {
                _inFlight--;
            }
        }
    }

    public ValueTask<IReadOnlyList<ConnectorCommandResult>> ExecuteBatchAsync(ConnectionId connection, IReadOnlyList<ConnectorCommand> commands, CancellationToken ct) =>
        throw new NotSupportedException("T2.03");

    public bool SupportsBatch(ConnectionId connection) => false;

    /// <summary>Completes the waiting call of a command.</summary>
    public void Complete(Guid commandId, ConnectorCommandResult result)
    {
        if (!_pending.TryRemove(commandId, out var waiter))
        {
            throw new InvalidOperationException($"No waiting call for {commandId}.");
        }

        waiter.TrySetResult(result);
    }

    /// <summary>Completes every waiting call (end of a test).</summary>
    public void ReleaseAll()
    {
        foreach (var id in _pending.Keys)
        {
            if (_pending.TryRemove(id, out var waiter))
            {
                waiter.TrySetResult(ConnectorCommandResult.Succeeded());
            }
        }
    }
}
