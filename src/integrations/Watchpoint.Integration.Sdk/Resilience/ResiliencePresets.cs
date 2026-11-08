using System.Net.Sockets;
using Polly;
using Polly.CircuitBreaker;
using Polly.Retry;
using Polly.Timeout;

namespace Watchpoint.Integration.Sdk.Resilience;

/// <summary>
/// Pre-configured Polly resilience pipelines (INT §10).
/// - Request: timeout + 2 retries on transient errors.
/// - Command: timeout only (no retry unless idempotent).
/// - Poll: timeout + circuit breaker opening after 5 failures for 30 s.
/// </summary>
public static class ResiliencePresets
{
    private static bool IsTransient(Exception ex) =>
        ex is HttpRequestException or SocketException or TimeoutException or TimeoutRejectedException;

    /// <summary>
    /// Creates a resilience pipeline for general requests (timeout + 2 retries on transient).
    /// </summary>
    /// <param name="timeout">Request timeout (default 10s).</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <returns>Configured resilience pipeline.</returns>
    public static ResiliencePipeline CreateRequestPipeline(
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
    {
        var builder = new ResiliencePipelineBuilder();
        if (timeProvider is not null)
        {
            builder.TimeProvider = timeProvider;
        }

        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(10),
        });

        builder.AddRetry(new RetryStrategyOptions
        {
            MaxRetryAttempts = 2,
            Delay = TimeSpan.FromMilliseconds(200),
            BackoffType = DelayBackoffType.Exponential,
            ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
        });

        return builder.Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for commands (timeout only, no retry unless idempotent).
    /// </summary>
    /// <param name="isIdempotent">Whether the command is idempotent and can be safely retried.</param>
    /// <param name="timeout">Command timeout (default 15s).</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <returns>Configured resilience pipeline.</returns>
    public static ResiliencePipeline CreateCommandPipeline(
        bool isIdempotent = false,
        TimeSpan? timeout = null,
        TimeProvider? timeProvider = null)
    {
        var builder = new ResiliencePipelineBuilder();
        if (timeProvider is not null)
        {
            builder.TimeProvider = timeProvider;
        }

        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(15),
        });

        if (isIdempotent)
        {
            builder.AddRetry(new RetryStrategyOptions
            {
                MaxRetryAttempts = 2,
                Delay = TimeSpan.FromMilliseconds(200),
                BackoffType = DelayBackoffType.Exponential,
                ShouldHandle = new PredicateBuilder().Handle<Exception>(IsTransient),
            });
        }

        return builder.Build();
    }

    /// <summary>
    /// Creates a resilience pipeline for periodic polling (timeout + circuit breaker opening after 5 failures for 30s).
    /// </summary>
    /// <param name="timeout">Poll timeout (default 30s).</param>
    /// <param name="failureThreshold">Number of consecutive failures to open circuit (default 5).</param>
    /// <param name="breakDuration">Break duration before half-open state (default 30s).</param>
    /// <param name="timeProvider">Time provider.</param>
    /// <param name="stateProvider">Optional circuit breaker state provider.</param>
    /// <returns>Configured resilience pipeline.</returns>
    public static ResiliencePipeline CreatePollPipeline(
        TimeSpan? timeout = null,
        int failureThreshold = 5,
        TimeSpan? breakDuration = null,
        TimeProvider? timeProvider = null,
        CircuitBreakerStateProvider? stateProvider = null)
    {
        var builder = new ResiliencePipelineBuilder();
        if (timeProvider is not null)
        {
            builder.TimeProvider = timeProvider;
        }

        builder.AddTimeout(new TimeoutStrategyOptions
        {
            Timeout = timeout ?? TimeSpan.FromSeconds(30),
        });

        builder.AddCircuitBreaker(new CircuitBreakerStrategyOptions
        {
            FailureRatio = 1.0,
            MinimumThroughput = failureThreshold,
            BreakDuration = breakDuration ?? TimeSpan.FromSeconds(30),
            SamplingDuration = TimeSpan.FromMinutes(2),
            ShouldHandle = new PredicateBuilder().Handle<Exception>(),
            StateProvider = stateProvider,
        });

        return builder.Build();
    }
}
