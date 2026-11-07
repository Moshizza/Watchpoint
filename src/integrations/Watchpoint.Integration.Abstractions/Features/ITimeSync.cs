using Watchpoint.Kernel.Results;

namespace Watchpoint.Integration.Abstractions.Features;

/// <summary>Reads and sets the clock of the integrated system (INT §6).</summary>
public interface ITimeSync
{
    /// <summary>Reads the system's clock.</summary>
    /// <param name="ct">Cancellation.</param>
    /// <returns>The clock reading.</returns>
    ValueTask<TimeSyncStatus> GetTimeAsync(CancellationToken ct);

    /// <summary>Sets the system's clock.</summary>
    /// <param name="utc">The time to set.</param>
    /// <param name="ct">Cancellation.</param>
    /// <returns>Success or failure.</returns>
    ValueTask<Result> SetTimeAsync(DateTimeOffset utc, CancellationToken ct);
}

/// <summary>A reading of the system's clock; the runtime computes the drift.</summary>
/// <param name="SystemTime">The time reported by the system.</param>
/// <param name="ReadAt">The node's time when the answer was received.</param>
/// <param name="RoundTrip">The request round trip, when measured.</param>
public sealed record TimeSyncStatus(DateTimeOffset SystemTime, DateTimeOffset ReadAt, TimeSpan? RoundTrip);
