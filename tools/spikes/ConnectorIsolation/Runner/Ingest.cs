using System.Diagnostics;
using System.Threading.Channels;
using Watchpoint.Spikes.ConnectorIsolation.Contract;

namespace Watchpoint.Spikes.ConnectorIsolation.Runner;

/// <summary>
/// Stand-in for the pipeline ingest (PIP): a bounded channel and one consumer that records the latency
/// report → ingest of every observation. Same code for both modes.
/// </summary>
internal sealed class Ingest
{
    private readonly Channel<Observation> _channel =
        Channel.CreateBounded<Observation>(new BoundedChannelOptions(100_000) { SingleReader = true, FullMode = BoundedChannelFullMode.Wait });
    private long[] _latencies = [];
    private int _count;
    private int _expected;
    private TaskCompletionSource _done = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public Ingest() => _ = Task.Run(ConsumeAsync);

    public ChannelWriter<Observation> Writer => _channel.Writer;

    /// <summary>Prepares a run of <paramref name="expected"/> observations; the task completes when all arrived.</summary>
    public Task Expect(int expected)
    {
        _latencies = new long[expected];
        _expected = expected;
        _done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Volatile.Write(ref _count, 0);
        return _done.Task;
    }

    /// <summary>Latencies of the last run, in Stopwatch ticks.</summary>
    public long[] Latencies => _latencies;

    /// <summary>A sink writing straight into the channel (in-process mode).</summary>
    public IObservationSink CreateDirectSink() => new DirectSink(_channel.Writer);

    private async Task ConsumeAsync()
    {
        await foreach (var o in _channel.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var index = _count;
            if (index < _expected)
            {
                _latencies[index] = Stopwatch.GetTimestamp() - o.Timestamp;
                _count = index + 1;
                if (index + 1 == _expected)
                {
                    _done.TrySetResult();
                }
            }
        }
    }

    private sealed class DirectSink(ChannelWriter<Observation> writer) : IObservationSink
    {
        public void Report(in Observation observation)
        {
            var spinner = default(SpinWait);
            while (!writer.TryWrite(observation))
            {
                spinner.SpinOnce();
            }
        }
    }
}
