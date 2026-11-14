using Microsoft.Extensions.Logging;
using Watchpoint.Integrations.Contracts;

namespace Watchpoint.Integrations.Internal.Packages;

/// <summary>
/// Watches the user package folder (spec IRT §4.1).
/// <list type="bullet">
/// <item>Every notification restarts a debounce timer; the scan runs when the folder has been quiet for
/// <see cref="IntegrationsOptions.Debounce"/>, so a copy in progress is not read half-way.</item>
/// <item>A file that is still locked or unreadable at scan time is retried after another debounce interval; temporary
/// names (dot files, anything not ending in <c>.wpkg</c>) are ignored until they are renamed into place.</item>
/// <item>Lost notifications (buffer overflow) and anything the watcher misses are covered by a rescan: immediately
/// requested on overflow, and every <see cref="IntegrationsOptions.RescanInterval"/>.</item>
/// </list>
/// Scans are serialised by the catalogue and compare fingerprints, so a rescan of an unchanged folder does nothing.
/// All timers use the injected <see cref="TimeProvider"/>.
/// </summary>
internal sealed class PackageWatcher(PackageCatalog catalog, IntegrationsOptions options, IFolderChangeSourceFactory sources,
    TimeProvider time, ILogger<PackageWatcher> logger) : IPackageWatcher, IAsyncDisposable
{
    private readonly Lock _lock = new();
    private readonly CancellationTokenSource _stop = new();
    private IFolderChangeSource? _source;
    private ITimer? _debounce;
    private ITimer? _periodic;
    private Task _scan = Task.CompletedTask;
    private bool _scanRequested;
    private bool _scanRunning;
    private bool _disposed;
    private long _notifications;
    private long _scans;

    /// <summary>Gets the number of folder notifications received.</summary>
    public long Notifications => Interlocked.Read(ref _notifications);

    /// <summary>Gets the number of scans completed since <see cref="StartAsync"/> (the initial one included).</summary>
    public long ScansCompleted => Interlocked.Read(ref _scans);

    /// <inheritdoc/>
    public async Task StartAsync(CancellationToken ct)
    {
        await catalog.ScanAsync(ct).ConfigureAwait(false);
        Interlocked.Increment(ref _scans);
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _debounce = time.CreateTimer(_ => RequestScan(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
            _periodic = time.CreateTimer(_ => RequestScan(), null, options.RescanInterval, options.RescanInterval);
            _source = sources.Create(options.PackagesPath);
            _source.Changed += OnChanged;
            _source.Overflowed += OnOverflowed;
            _source.Start();
        }
    }

    /// <inheritdoc/>
    public async ValueTask DisposeAsync()
    {
        Task scan;
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _source?.Dispose();
            _debounce?.Dispose();
            _periodic?.Dispose();
            scan = _scan;
        }

        await _stop.CancelAsync().ConfigureAwait(false);
        try
        {
            await scan.ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }

        _stop.Dispose();
    }

    private void OnChanged()
    {
        Interlocked.Increment(ref _notifications);
        ArmDebounce();
    }

    private void OnOverflowed(Exception? exception)
    {
        // Events were lost: what changed is unknown, so the whole folder is scanned again (after the same quiet time,
        // because an overflow usually means a large copy is still running).
        IntegrationLog.WatcherOverflow(logger, exception, options.PackagesPath);
        Interlocked.Increment(ref _notifications);
        ArmDebounce();
    }

    private void ArmDebounce()
    {
        lock (_lock)
        {
            if (!_disposed)
            {
                _debounce?.Change(options.Debounce, Timeout.InfiniteTimeSpan);
            }
        }
    }

    private void RequestScan()
    {
        lock (_lock)
        {
            if (_disposed)
            {
                return;
            }

            _scanRequested = true;
            if (_scanRunning)
            {
                return; // the running loop picks the request up
            }

            _scanRunning = true;
            _scan = Task.Run(ScanLoopAsync);
        }
    }

    private async Task ScanLoopAsync()
    {
        while (true)
        {
            lock (_lock)
            {
                if (!_scanRequested || _disposed)
                {
                    _scanRunning = false;
                    return;
                }

                _scanRequested = false;
            }

            try
            {
                var outcome = await catalog.ScanAsync(_stop.Token).ConfigureAwait(false);
                if (outcome.Deferred > 0)
                {
                    ArmDebounce(); // still being written: look again once it is quiet
                }
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested)
            {
                lock (_lock)
                {
                    _scanRunning = false;
                }

                return;
            }
            catch (Exception ex)
            {
                // The periodic rescan retries; the loop itself must survive.
                IntegrationLog.ScanFailed(logger, ex);
            }

            Interlocked.Increment(ref _scans);
        }
    }
}
