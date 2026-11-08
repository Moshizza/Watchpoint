using System.Net.Sockets;
using System.Security.Authentication;
using Microsoft.Extensions.Logging;
using Watchpoint.Integration.Abstractions;
using Watchpoint.Integration.Abstractions.Context;
using Watchpoint.Integration.Abstractions.Features;
using Watchpoint.Integration.Abstractions.Model;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Values;

namespace Watchpoint.Integration.Sdk;

/// <summary>
/// Base class providing lifecycle and reconnection loops for connectors (INT §9).
/// </summary>
public abstract class ConnectorBase : IConnector
{
    private CancellationTokenSource? _loopCts;
    private Task? _loopTask;
    private bool _disposed;

    /// <summary>Gets the connector context provided during initialization.</summary>
    protected IConnectorContext Context { get; private set; } = null!;

    /// <summary>Gets the logger for this connector.</summary>
    protected ILogger Logger { get; private set; } = null!;

    /// <inheritdoc />
    public virtual ValueTask<Result> InitializeAsync(IConnectorContext context, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(context);
        Context = context;
        Logger = context.Logging.CreateLogger(GetType().FullName ?? GetType().Name);
        return ValueTask.FromResult(Result.Success());
    }

    /// <inheritdoc />
    public abstract ValueTask<ConnectionTestResult> TestAsync(CancellationToken ct);

    /// <inheritdoc />
    public abstract ValueTask<InventorySnapshot> DescribeAsync(CancellationToken ct);

    /// <summary>Connects once to the target system. Throws on failure.</summary>
    /// <param name="ct">Cancellation token.</param>
    protected abstract ValueTask ConnectOnceAsync(CancellationToken ct);

    /// <summary>Runs the main connected loop (e.g. streaming events). Returns when the connection is lost or requested to stop.</summary>
    /// <param name="ct">Cancellation token.</param>
    protected abstract ValueTask RunConnectedAsync(CancellationToken ct);

    /// <summary>Disconnects from the system. Called upon graceful stop or connection teardown.</summary>
    /// <param name="ct">Cancellation token.</param>
    protected virtual ValueTask DisconnectAsync(CancellationToken ct) => ValueTask.CompletedTask;

    /// <summary>Invoked immediately upon establishing a connection. Calls refresh and event catch-up when implemented.</summary>
    /// <param name="ct">Cancellation token.</param>
    protected virtual async ValueTask OnConnectedAsync(CancellationToken ct)
    {
        if (this is IStateRefresh refresh)
        {
            await refresh.RefreshAsync(RefreshScope.All, ct).ConfigureAwait(false);
        }

        if (this is IEventCatchUp catchUp)
        {
            await catchUp.CatchUpAsync(ct).ConfigureAwait(false);
        }
    }

    /// <summary>Calculates the delay before reconnect attempt <paramref name="attempt"/> (1, 2, 4, 8, 16, 32, max 60 s ±20% jitter).</summary>
    /// <param name="attempt">The 1-based reconnect attempt number.</param>
    /// <returns>The calculated back-off delay.</returns>
    protected internal virtual TimeSpan GetReconnectDelay(int attempt)
    {
        var clampedAttempt = Math.Max(1, attempt);
        // Exponential back-off: 1, 2, 4, 8, 16, 32, 60s
        var baseSeconds = clampedAttempt >= 7 ? 60.0 : Math.Pow(2, clampedAttempt - 1);
        if (baseSeconds > 60.0)
        {
            baseSeconds = 60.0;
        }

        // ±20% jitter
        var jitterFactor = 0.8 + (Random.Shared.NextDouble() * 0.4);
        return TimeSpan.FromSeconds(baseSeconds * jitterFactor);
    }

    /// <summary>Maps an exception encountered during connection or execution to a failure category and message.</summary>
    /// <param name="exception">The exception to map.</param>
    /// <returns>A tuple of the failure category and connector message.</returns>
    protected internal virtual (FailureCategory Category, ConnectorMessage Message) MapFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var actual = exception is AggregateException agg && agg.InnerExceptions.Count == 1
            ? agg.InnerExceptions[0]
            : exception;

        return actual switch
        {
            SocketException => (FailureCategory.Unreachable, ConnectorMessage.Of("connection.unreachable", ("reason", actual.Message))),
            AuthenticationException => (FailureCategory.TlsError, ConnectorMessage.Of("connection.tls_error", ("reason", actual.Message))),
            TimeoutException => (FailureCategory.Timeout, ConnectorMessage.Of("connection.timeout", ("reason", actual.Message))),
            OperationCanceledException => (FailureCategory.ConnectionDown, ConnectorMessage.Of("connection.canceled", ("reason", actual.Message))),
            _ => (FailureCategory.InternalError, ConnectorMessage.Of("connection.error", ("reason", actual.Message))),
        };
    }

    /// <inheritdoc />
    public ValueTask StartAsync(CancellationToken ct)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _loopCts = CancellationTokenSource.CreateLinkedTokenSource(ct, Context.Stopping);
        _loopTask = Task.Run(() => RunLoopAsync(_loopCts.Token), CancellationToken.None);
        return ValueTask.CompletedTask;
    }

    private async Task RunLoopAsync(CancellationToken ct)
    {
        var attempt = 0;

        while (!ct.IsCancellationRequested)
        {
            attempt++;
            try
            {
                Context.Health.Report(ConnectionStatus.Connecting);
                await ConnectOnceAsync(ct).ConfigureAwait(false);

                // Successfully connected
                attempt = 0;
                Context.Health.Report(ConnectionStatus.Connected);

                await OnConnectedAsync(ct).ConfigureAwait(false);
                await RunConnectedAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                var (_, msg) = MapFailure(ex);
                Context.Health.Report(ConnectionStatus.Disconnected, msg);
            }
            finally
            {
                // Disconnected transition
                try
                {
                    Context.Sink.ReportServerHealth("root", Connectivity.Unknown, reason: "connectionLost");
                }
                catch
                {
                    // Ignore reporting failures on teardown
                }
            }

            if (ct.IsCancellationRequested)
            {
                break;
            }

            var delay = GetReconnectDelay(attempt);
            try
            {
                await Task.Delay(delay, Context.Time, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private int _stopped;

    /// <inheritdoc />
    public async ValueTask StopAsync(CancellationToken ct)
    {
        if (Interlocked.Exchange(ref _stopped, 1) != 0)
        {
            return;
        }

        if (_loopCts is not null)
        {
            await _loopCts.CancelAsync().ConfigureAwait(false);
        }

        if (_loopTask is not null)
        {
            try
            {
                await _loopTask.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                // Expected when stopping
            }
            catch (Exception)
            {
                // Background loop exceptions are swallowed during StopAsync
            }
        }

        await DisconnectAsync(ct).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public virtual async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        await StopAsync(CancellationToken.None).ConfigureAwait(false);
        _loopCts?.Dispose();
        GC.SuppressFinalize(this);
    }
}
