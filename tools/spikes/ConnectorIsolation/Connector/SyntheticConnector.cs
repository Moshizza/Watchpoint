using System.Diagnostics;
using System.Runtime.CompilerServices;
using Watchpoint.Spikes.ConnectorIsolation.Contract;

namespace Watchpoint.Spikes.ConnectorIsolation.Connector;

/// <summary>A synthetic connector: emits property reports, echoes commands, misbehaves on request.</summary>
public sealed class SyntheticConnector : ISpikeConnector
{
    private IObservationSink? _sink;

    /// <inheritdoc/>
    public void Initialize(IObservationSink sink) => _sink = sink;

    /// <inheritdoc/>
    public Task EmitAsync(int count, int ratePerSecond, CancellationToken ct) =>
        Task.Factory.StartNew(() => Emit(count, ratePerSecond, ct), ct, TaskCreationOptions.LongRunning, TaskScheduler.Default);

    /// <inheritdoc/>
    public ValueTask<string> ExecuteAsync(string command, CancellationToken ct) => ValueTask.FromResult(command);

    /// <inheritdoc/>
    public void Misbehave(Misbehaviour kind)
    {
        switch (kind)
        {
            case Misbehaviour.BackgroundThreadException:
                new Thread(() => throw new InvalidOperationException("connector bug on a background thread")) { IsBackground = true }.Start();
                break;
            case Misbehaviour.StackOverflow:
                new Thread(() => Recurse(0), maxStackSize: 256 * 1024) { IsBackground = true }.Start();
                break;
            case Misbehaviour.FailFast:
                Environment.FailFast("connector called FailFast");
                break;
            case Misbehaviour.Hang:
                new Thread(Spin) { IsBackground = true }.Start();
                break;
            case Misbehaviour.LeakIntoSharedType:
                SharedEvents.Tick += OnTick;
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(kind));
        }
    }

    private void OnTick(object? sender, EventArgs e) => _sink?.Report(new Observation(0, 0, Stopwatch.GetTimestamp()));

    private void Emit(int count, int ratePerSecond, CancellationToken ct)
    {
        var sink = _sink ?? throw new InvalidOperationException("not initialised");
        var start = Stopwatch.GetTimestamp();
        var ticksPerReport = ratePerSecond > 0 ? (double)Stopwatch.Frequency / ratePerSecond : 0;
        for (var i = 0; i < count && !ct.IsCancellationRequested; i++)
        {
            if (ticksPerReport > 0)
            {
                var due = start + (long)(i * ticksPerReport);
                while (Stopwatch.GetTimestamp() < due)
                {
                    if (due - Stopwatch.GetTimestamp() > Stopwatch.Frequency / 1000)
                    {
                        Thread.Sleep(1);
                    }
                    else
                    {
                        Thread.SpinWait(20);
                    }
                }
            }

            sink.Report(new Observation(i % 10_000, i, Stopwatch.GetTimestamp()));
        }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Recurse(int depth) => Recurse(depth + 1) + 1;

    private static void Spin()
    {
        while (true)
        {
            Thread.SpinWait(1000);
        }
    }
}
