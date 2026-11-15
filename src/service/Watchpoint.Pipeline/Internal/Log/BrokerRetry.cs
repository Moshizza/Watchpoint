using Microsoft.Extensions.Logging;
using Watchpoint.Pipeline.Contracts;

namespace Watchpoint.Pipeline.Internal.Log;

/// <summary>
/// Repeats a broker call while the broker is unavailable: every attempt is bounded by <c>OperationTimeout</c>, attempts
/// are spaced with a growing delay, and after <c>AppendTimeout</c> the call fails with <c>pipeline.log.unavailable</c>.
/// </summary>
internal static class BrokerRetry
{
    private static readonly TimeSpan _logInterval = TimeSpan.FromSeconds(5);

    public static async ValueTask<T> RunAsync<T>(
        Func<CancellationToken, ValueTask<T>> operation,
        PipelineOptions options,
        TimeProvider timeProvider,
        ILogger logger,
        CancellationToken ct)
    {
        using var deadline = new CancellationTokenSource(options.AppendTimeout, timeProvider);
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct, deadline.Token);
        var delay = options.RetryDelayMin;
        var lastLog = long.MinValue;
        var reason = "no answer";
        Exception? last = null;
        while (true)
        {
            using var attempt = new CancellationTokenSource(options.OperationTimeout, timeProvider);
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(budget.Token, attempt.Token);
            try
            {
                return await operation(linked.Token).ConfigureAwait(false);
            }
            catch (BrokerUnavailableException ex)
            {
                last = ex;
                reason = ex.Message;
            }
            catch (OperationCanceledException) when (!budget.IsCancellationRequested)
            {
                reason = "the call timed out";
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PipelineException(PipelineErrors.LogUnavailable(reason), last);
            }

            LogThrottled(logger, timeProvider, ref lastLog, reason);
            try
            {
                await Task.Delay(delay, timeProvider, budget.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new PipelineException(PipelineErrors.LogUnavailable(reason), last);
            }

            delay = Next(delay, options);
        }
    }

    public static TimeSpan Next(TimeSpan delay, PipelineOptions options) => Next(delay, options.RetryDelayMax);

    /// <summary>Doubles the delay up to <paramref name="max"/>.</summary>
    public static TimeSpan Next(TimeSpan delay, TimeSpan max) => delay + delay < max ? delay + delay : max;

    /// <summary>Logs event 1602 at most once per 5 s per caller.</summary>
    public static void LogThrottled(ILogger logger, TimeProvider timeProvider, ref long lastLog, string reason)
    {
        var now = timeProvider.GetTimestamp();
        if (lastLog == long.MinValue || timeProvider.GetElapsedTime(lastLog, now) >= _logInterval)
        {
            lastLog = now;
            PipelineLog.LogUnavailable(logger, reason);
        }
    }
}
