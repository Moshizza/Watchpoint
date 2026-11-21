using Watchpoint.Kernel.Time;

namespace Watchpoint.Pipeline.Tests.Fakes;

/// <summary>A lease gate the test grants and revokes (the cluster lease manager of CLU, K §6.1).</summary>
internal sealed class FakeLeaseGate : ILeaseGate, IDisposable
{
    private readonly object _lock = new();
    private TaskCompletionSource<CancellationToken> _grant = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _held;

    public List<string> Waits { get; } = [];

    public ValueTask<CancellationToken> WaitUntilHeldAsync(string lease, CancellationToken ct)
    {
        lock (_lock)
        {
            Waits.Add(lease);
            return new ValueTask<CancellationToken>(_grant.Task.WaitAsync(ct));
        }
    }

    /// <summary>This node now holds the lease.</summary>
    public void Grant()
    {
        lock (_lock)
        {
            _held = new CancellationTokenSource();
            _grant.SetResult(_held.Token);
        }
    }

    /// <summary>This node lost the lease: the held token is cancelled and the next wait blocks until <see cref="Grant"/>.</summary>
    public void Revoke()
    {
        CancellationTokenSource? held;
        lock (_lock)
        {
            held = _held;
            _held = null;
            _grant = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        }

        held?.Cancel();
        held?.Dispose();
    }

    public void Dispose() => Revoke();
}
