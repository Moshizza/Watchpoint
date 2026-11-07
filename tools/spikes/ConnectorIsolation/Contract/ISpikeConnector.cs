namespace Watchpoint.Spikes.ConnectorIsolation.Contract;

/// <summary>The connector contract of the spike (a tiny stand-in for INT's <c>IConnector</c>).</summary>
public interface ISpikeConnector
{
    /// <summary>Gives the connector its sink.</summary>
    void Initialize(IObservationSink sink);

    /// <summary>Reports <paramref name="count"/> observations; <paramref name="ratePerSecond"/> 0 = as fast as possible.</summary>
    Task EmitAsync(int count, int ratePerSecond, CancellationToken ct);

    /// <summary>A command: returns its argument (round-trip measurement).</summary>
    ValueTask<string> ExecuteAsync(string command, CancellationToken ct);

    /// <summary>Misbehaves on purpose.</summary>
    void Misbehave(Misbehaviour kind);
}

/// <summary>What the connector reports to (sink rule S1: never blocks for long).</summary>
public interface IObservationSink
{
    /// <summary>Reports one property value.</summary>
    void Report(in Observation observation);
}

/// <summary>A property report; <see cref="Timestamp"/> is <see cref="System.Diagnostics.Stopwatch.GetTimestamp"/> (system-wide monotonic clock).</summary>
public readonly record struct Observation(int Device, double Value, long Timestamp);

/// <summary>Faults a connector can produce.</summary>
public enum Misbehaviour
{
    /// <summary>Unhandled exception on a thread the connector started.</summary>
    BackgroundThreadException = 1,

    /// <summary>Unbounded recursion: the runtime cannot catch it.</summary>
    StackOverflow = 2,

    /// <summary><see cref="Environment.FailFast(string)"/> (native libraries do the equivalent with abort()).</summary>
    FailFast = 3,

    /// <summary>Spins a CPU forever on a background thread.</summary>
    Hang = 4,

    /// <summary>Subscribes to <see cref="SharedEvents.Tick"/> and never unsubscribes (blocks unloading).</summary>
    LeakIntoSharedType = 5,
}

/// <summary>A static event in a shared assembly: subscribing from a package roots its load context.</summary>
public static class SharedEvents
{
    /// <summary>Raised by nobody; only subscriptions matter.</summary>
    public static event EventHandler? Tick;

    /// <summary>Raises <see cref="Tick"/>.</summary>
    public static void Raise() => Tick?.Invoke(null, EventArgs.Empty);
}
