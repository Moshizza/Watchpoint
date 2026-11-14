namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>A cancellation token that fires when the caller's token does or after a time measured with the <see cref="TimeProvider"/>.</summary>
internal sealed class Deadline : IDisposable
{
    private readonly CancellationTokenSource _timer;
    private readonly CancellationTokenSource _linked;

    public Deadline(TimeSpan timeout, TimeProvider timeProvider, CancellationToken ct)
    {
        _timer = new CancellationTokenSource(timeout, timeProvider);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(ct, _timer.Token);
    }

    public CancellationToken Token => _linked.Token;

    public void Dispose()
    {
        _linked.Dispose();
        _timer.Dispose();
    }
}
