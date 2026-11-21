using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Values;

namespace Watchpoint.History.Contracts;

/// <summary>A current value of a property whose history policy is <c>periodic</c>.</summary>
/// <param name="Device">The device.</param>
/// <param name="Path">The property path.</param>
/// <param name="Value">The current value.</param>
/// <param name="Quality">The quality.</param>
/// <param name="SourceTime">The source time of the value.</param>
public sealed record PeriodicPropertyValue(DeviceId Device, string Path, WpValue Value, Quality Quality, DateTimeOffset? SourceTime);

/// <summary>
/// Seam to the state module (STA): the current values of the properties that are sampled periodically. The
/// <c>PeriodicSampler</c> runs when an implementation is registered.
/// </summary>
public interface IPeriodicPropertySource
{
    /// <summary>Gets the sampling intervals in use (the <c>interval</c> of the periodic policies).</summary>
    IReadOnlyCollection<TimeSpan> Intervals { get; }

    /// <summary>Returns the current values of the properties sampled every <paramref name="interval"/>.</summary>
    /// <param name="interval">One of <see cref="Intervals"/>.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The values.</returns>
    IAsyncEnumerable<PeriodicPropertyValue> GetValuesAsync(TimeSpan interval, CancellationToken ct);
}
