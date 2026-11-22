using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Watchpoint.History.Contracts;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.History.Internal.Policies;

/// <summary>
/// Stores the current values of <c>periodic</c> properties from the state module every interval, aligned to interval boundaries
/// (spec HIS §6). A sample has the boundary as its time and an id derived from (device, path, boundary): sampling a boundary twice
/// stores it once.
/// </summary>
internal sealed class PeriodicSampler(IPeriodicPropertySource source, IHistoryStore store, TimeProvider time, ILogger<PeriodicSampler> logger)
{
    /// <summary>The first interval boundary after <paramref name="now"/>; boundaries are multiples of the interval counted from UTC midnight (so a day is divided evenly by any interval that divides it).</summary>
    /// <param name="now">The time.</param>
    /// <param name="interval">The interval (positive).</param>
    /// <returns>The next boundary, strictly after <paramref name="now"/>.</returns>
    public static DateTimeOffset NextBoundary(DateTimeOffset now, TimeSpan interval)
    {
        var ticks = now.UtcTicks;
        return new DateTimeOffset((ticks / interval.Ticks + 1) * interval.Ticks, TimeSpan.Zero);
    }

    /// <summary>The boundary at or before <paramref name="now"/>.</summary>
    /// <param name="now">The time.</param>
    /// <param name="interval">The interval (positive).</param>
    /// <returns>The boundary.</returns>
    public static DateTimeOffset BoundaryAtOrBefore(DateTimeOffset now, TimeSpan interval) =>
        new(now.UtcTicks / interval.Ticks * interval.Ticks, TimeSpan.Zero);

    /// <summary>Runs one loop per interval until cancelled.</summary>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>A task completing when cancelled.</returns>
    public Task RunAsync(CancellationToken ct) => Task.WhenAll(source.Intervals.Where(i => i > TimeSpan.Zero).Select(i => LoopAsync(i, ct)));

    /// <summary>Stores the current values of the properties sampled every <paramref name="interval"/> with time <paramref name="boundary"/>.</summary>
    /// <param name="interval">The interval.</param>
    /// <param name="boundary">The boundary time.</param>
    /// <param name="ct">The cancellation token.</param>
    /// <returns>The number of rows inserted.</returns>
    public async Task<int> SampleAsync(TimeSpan interval, DateTimeOffset boundary, CancellationToken ct)
    {
        List<HistoryPropertyRow> rows = [];
        await foreach (var value in source.GetValuesAsync(interval, ct).ConfigureAwait(false))
        {
            var columns = Writers.ValueColumns.From(value.Value);
            rows.Add(new HistoryPropertyRow(
                boundary, value.Device.Value, value.Path, columns.Num, columns.Text, columns.Bool, columns.Json, (short)value.Quality, value.SourceTime,
                SampleId(value.Device.Value, value.Path, boundary)));
        }

        return await store.WritePropertyChangesAsync(rows, ct).ConfigureAwait(false);
    }

    /// <summary>The id of the sample of a property at a boundary.</summary>
    /// <param name="device">The device.</param>
    /// <param name="path">The path.</param>
    /// <param name="boundary">The boundary.</param>
    /// <returns>A stable id.</returns>
    public static Guid SampleId(Guid device, string path, DateTimeOffset boundary)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{device:D}|{path}|{boundary.UtcTicks}"));
        return new Guid(hash.AsSpan(0, 16));
    }

    private async Task LoopAsync(TimeSpan interval, CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var boundary = NextBoundary(time.GetUtcNow(), interval);
            try
            {
                await Task.Delay(boundary - time.GetUtcNow(), time, ct).ConfigureAwait(false);
                await SampleAsync(interval, boundary, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                return;
            }
#pragma warning disable CA1031 // A failed sample is skipped: the next boundary tries again.
            catch (Exception ex)
#pragma warning restore CA1031
            {
                HistoryLog.WriteFailed(logger, "periodic-sampler", ex.GetType().Name);
            }
        }
    }
}
