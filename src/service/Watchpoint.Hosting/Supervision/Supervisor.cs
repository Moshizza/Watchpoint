using System.Diagnostics.Metrics;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;

namespace Watchpoint.Hosting.Supervision;

/// <summary>
/// The process supervisor (spec HST §7): starts helper processes, restarts them with back-off, stops them in
/// reverse start order at shutdown, and makes sure none outlives the host.
/// </summary>
public sealed partial class Supervisor : ISupervisor, IAsyncDisposable
{
    /// <summary>The name of the meter of the child metrics (spec HST §11).</summary>
    public const string MeterName = "Watchpoint.Host";

    private readonly SupervisorRuntime _runtime;
    private readonly ChildProcessJanitor _janitor;
    private readonly bool _ownsJanitor;
    private readonly ReadinessProbes? _ownedProber;
    private readonly Meter _meter = new(MeterName);
    private readonly Lock _gate = new();
    private readonly List<SupervisedProcess> _processes = [];
    private readonly HashSet<string> _reserved = new(StringComparer.Ordinal);
    private bool _stopping;
    private bool _disposed;

    /// <summary>Initializes a new instance of the <see cref="Supervisor"/> class.</summary>
    /// <param name="options">The options.</param>
    /// <param name="loggerFactory">The logger factory (children log to <see cref="ProcessSpec.LogCategory"/>).</param>
    /// <param name="timeProvider">The time provider; the system one when <see langword="null"/>.</param>
    /// <param name="janitor">The janitor of the run folder; one is created (and owned) when <see langword="null"/>.</param>
    public Supervisor(SupervisorOptions options, ILoggerFactory loggerFactory, TimeProvider? timeProvider = null, ChildProcessJanitor? janitor = null)
        : this(options, loggerFactory, timeProvider ?? TimeProvider.System, janitor, launcher: null, prober: null)
    {
    }

    internal Supervisor(
        SupervisorOptions options,
        ILoggerFactory loggerFactory,
        TimeProvider timeProvider,
        ChildProcessJanitor? janitor,
        IProcessLauncher? launcher,
        IReadinessProber? prober)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        _ownsJanitor = janitor is null;
        _janitor = janitor ?? new ChildProcessJanitor(options.RunDirectory, loggerFactory.CreateLogger<ChildProcessJanitor>(), timeProvider);
        if (prober is null)
        {
            _ownedProber = new ReadinessProbes(timeProvider, options.ProbeInterval);
        }

        _runtime = new SupervisorRuntime(
            launcher ?? new SystemProcessLauncher(_janitor),
            prober ?? _ownedProber!,
            _janitor,
            timeProvider,
            options,
            loggerFactory.CreateLogger<Supervisor>(),
            loggerFactory,
            _meter.CreateCounter<long>("watchpoint.host.child.restarts", "{restarts}", "Restarts of supervised child processes"));
        _meter.CreateObservableGauge("watchpoint.host.child.state", ObserveStates, description: "State of supervised child processes (1 for the current state)");

        // Last line of defence when the process exits without disposing the host (Environment.Exit, Main returning).
        AppDomain.CurrentDomain.ProcessExit += OnProcessExit;
    }

    /// <inheritdoc />
    public IReadOnlyCollection<ISupervisedProcessHandle> Processes
    {
        get
        {
            lock (_gate)
            {
                return [.. _processes];
            }
        }
    }

    /// <inheritdoc />
    public Task<ISupervisedProcessHandle> StartAsync(ProcessSpec spec, CancellationToken ct) => StartAsync(spec, null, ct);

    /// <summary>As <see cref="StartAsync(ProcessSpec, CancellationToken)"/>; <paramref name="onCreated"/> sees the handle before the first transition.</summary>
    internal async Task<ISupervisedProcessHandle> StartAsync(ProcessSpec spec, Action<ISupervisedProcessHandle>? onCreated, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(spec);
        Validate(spec);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_stopping)
            {
                throw new InvalidOperationException("The supervisor is shutting down.");
            }

            if (!_reserved.Add(spec.Name))
            {
                throw new InvalidOperationException($"A process named '{spec.Name}' is already supervised.");
            }
        }

        SupervisedProcess process;
        try
        {
            if (!File.Exists(spec.Executable))
            {
                throw new SupervisorException(
                    HostErrors.ExecutableMissing(spec.Name, spec.Executable), $"Executable of process '{spec.Name}' does not exist: {spec.Executable}");
            }

            // A process of this name left by a host that died would hold the same ports and files.
            await _janitor.CleanupAsync(spec.Name, ct).ConfigureAwait(false);
            process = new SupervisedProcess(spec, _runtime, Remove);
            lock (_gate)
            {
                if (_stopping)
                {
                    throw new InvalidOperationException("The supervisor is shutting down.");
                }

                _processes.Add(process);
            }
        }
        catch
        {
            lock (_gate)
            {
                _reserved.Remove(spec.Name);
            }

            throw;
        }

        onCreated?.Invoke(process);
        process.Start();
        try
        {
            await process.FirstOutcome.WaitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // The caller gave up on the start: it gets no handle, so nothing may stay behind.
            await process.StopAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
            throw;
        }

        return process;
    }

    /// <summary>
    /// Stops every child, last started first, one after the other; refuses new starts from now on. Each stop is
    /// bounded by the child's grace period plus the kill timeout; cancelling <paramref name="ct"/> kills what is left.
    /// </summary>
    /// <param name="ct">Cancelling skips the remaining grace periods.</param>
    /// <returns>A task completing when no child is left.</returns>
    public async Task StopAllAsync(CancellationToken ct)
    {
        SupervisedProcess[] processes;
        lock (_gate)
        {
            _stopping = true;
            processes = [.. _processes];
        }

        for (var i = processes.Length - 1; i >= 0; i--)
        {
            await processes[i].StopAsync(ct).ConfigureAwait(false);
        }
    }

    /// <inheritdoc />
    public async ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
        }

        // Normally nothing is left (the shutdown task ran); otherwise kill without grace.
        await StopAllAsync(new CancellationToken(canceled: true)).ConfigureAwait(false);
        AppDomain.CurrentDomain.ProcessExit -= OnProcessExit;
        _meter.Dispose();
        _ownedProber?.Dispose();
        if (_ownsJanitor)
        {
            _janitor.Dispose();
        }
    }

    [GeneratedRegex("^[a-z0-9][a-z0-9._-]{0,63}$", RegexOptions.CultureInvariant)]
    private static partial Regex NamePattern();

    private static void Validate(ProcessSpec spec)
    {
        if (spec.Name is null || !NamePattern().IsMatch(spec.Name))
        {
            throw new ArgumentException("Process name must be 1–64 characters: lowercase letters, digits, '.', '_' or '-'.", nameof(spec));
        }

        if (string.IsNullOrEmpty(spec.Executable) || !Path.IsPathFullyQualified(spec.Executable))
        {
            throw new ArgumentException($"Executable of process '{spec.Name}' must be an absolute path.", nameof(spec));
        }

        if (spec.Limits is not null)
        {
            throw new NotSupportedException("Resource limits are not supported before T4.01.");
        }

        if (spec.StopGracePeriod < TimeSpan.Zero)
        {
            throw new ArgumentException("StopGracePeriod must not be negative.", nameof(spec));
        }
    }

    private void Remove(SupervisedProcess process)
    {
        lock (_gate)
        {
            _processes.Remove(process);
            _reserved.Remove(process.Name);
        }
    }

    private void OnProcessExit(object? sender, EventArgs e)
    {
        SupervisedProcess[] processes;
        lock (_gate)
        {
            processes = [.. _processes];
        }

        foreach (var process in processes)
        {
            process.KillNow();
        }
    }

    private IEnumerable<Measurement<int>> ObserveStates()
    {
        SupervisedProcess[] processes;
        lock (_gate)
        {
            processes = [.. _processes];
        }

        foreach (var process in processes)
        {
            var current = process.State;
            foreach (var state in Enum.GetValues<SupervisedProcessState>())
            {
                yield return new Measurement<int>(
                    state == current ? 1 : 0,
                    new KeyValuePair<string, object?>("child", process.Name),
                    new KeyValuePair<string, object?>("state", state.ToString()));
            }
        }
    }
}
