using Microsoft.Extensions.Time.Testing;
using Watchpoint.Integration.Abstractions.Context;

namespace Watchpoint.Integration.Testing.Fakes;

/// <summary>
/// Fake implementation of <see cref="IConnectorScheduler"/> driven by <see cref="FakeTimeProvider"/>.
/// </summary>
public sealed class TestConnectorScheduler : IConnectorScheduler
{
    private readonly FakeTimeProvider _time;
    private readonly List<ScheduledJob> _jobs = [];
    private readonly object _lock = new();

    /// <summary>Initializes a new instance of <see cref="TestConnectorScheduler"/>.</summary>
    public TestConnectorScheduler(FakeTimeProvider time)
    {
        _time = time;
    }

    /// <inheritdoc />
    public IDisposable SchedulePolling(string name, TimeSpan interval, Func<CancellationToken, ValueTask> work, PollingOptions? options = null)
    {
        var job = new ScheduledJob(name, interval, work, options ?? new PollingOptions());
        lock (_lock)
        {
            _jobs.Add(job);
        }
        return job;
    }

    /// <summary>Advances virtual time and triggers all registered polling jobs.</summary>
    public async ValueTask TriggerAsync(TimeSpan delta, CancellationToken ct = default)
    {
        _time.Advance(delta);
        List<ScheduledJob> snapshot;
        lock (_lock)
        {
            snapshot = _jobs.Where(j => !j.IsDisposed).ToList();
        }

        foreach (var job in snapshot)
        {
            try
            {
                await job.Work(ct);
            }
            catch when (!ct.IsCancellationRequested)
            {
                // Polling loop errors should be absorbed according to INT-C-038
            }
        }
    }

    private sealed class ScheduledJob : IDisposable
    {
        public string Name { get; }
        public TimeSpan Interval { get; }
        public Func<CancellationToken, ValueTask> Work { get; }
        public PollingOptions Options { get; }
        public bool IsDisposed { get; private set; }

        public ScheduledJob(string name, TimeSpan interval, Func<CancellationToken, ValueTask> work, PollingOptions options)
        {
            Name = name;
            Interval = interval;
            Work = work;
            Options = options;
        }

        public void Dispose()
        {
            IsDisposed = true;
        }
    }
}
