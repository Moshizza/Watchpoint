using System.Threading.Channels;
using Watchpoint.Spikes.ConnectorIsolation.Contract;
using Watchpoint.Spikes.ConnectorIsolation.Protocol;
using Observation = Watchpoint.Spikes.ConnectorIsolation.Contract.Observation;

namespace Watchpoint.Spikes.ConnectorIsolation.Host;

/// <summary>
/// The host-side sink (IHO §5.3): buffers reports and sends <see cref="ObservationBatch"/> messages of at most
/// <see cref="MaxBatch"/> items. Policy <c>timer</c>: a batch every 50 ms or when 500 items are waiting (the spec's
/// rule). Policy <c>eager</c>: send as soon as the previous batch is written, with whatever has accumulated.
/// </summary>
internal sealed class BatchingSink : IObservationSink, IAsyncDisposable
{
    public const int MaxBatch = 500;
    private static readonly TimeSpan _window = TimeSpan.FromMilliseconds(50);

    private readonly Channel<Observation> _buffer =
        Channel.CreateBounded<Observation>(new BoundedChannelOptions(100_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private readonly ChannelWriter<HostToNode> _outgoing;
    private readonly bool _timer;
    private readonly Task _loop;

    public BatchingSink(ChannelWriter<HostToNode> outgoing, bool timer)
    {
        _outgoing = outgoing;
        _timer = timer;
        _loop = Task.Run(LoopAsync);
    }

    public void Report(in Observation observation)
    {
        // Back-pressure: a full buffer makes the connector wait (a real sink applies S2 rules instead).
        var spinner = default(SpinWait);
        while (!_buffer.Writer.TryWrite(observation))
        {
            spinner.SpinOnce();
        }
    }

    public async ValueTask DisposeAsync()
    {
        _buffer.Writer.TryComplete();
        await _loop.ConfigureAwait(false);
    }

    private async Task LoopAsync()
    {
        var reader = _buffer.Reader;
        while (await reader.WaitToReadAsync().ConfigureAwait(false))
        {
            var batch = new ObservationBatch();
            if (_timer)
            {
                using var window = new CancellationTokenSource(_window);
                try
                {
                    while (batch.Items.Count < MaxBatch && await reader.WaitToReadAsync(window.Token).ConfigureAwait(false))
                    {
                        Drain(reader, batch);
                    }
                }
                catch (OperationCanceledException)
                {
                    // window elapsed: send what we have
                }
            }
            else
            {
                Drain(reader, batch);
            }

            await _outgoing.WriteAsync(new HostToNode { Observations = batch }).ConfigureAwait(false);
        }
    }

    private static void Drain(ChannelReader<Observation> reader, ObservationBatch batch)
    {
        while (batch.Items.Count < MaxBatch && reader.TryRead(out var o))
        {
            batch.Items.Add(new Protocol.Observation { Device = o.Device, Value = o.Value, Timestamp = o.Timestamp });
        }
    }
}
