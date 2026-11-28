using System.Collections.Concurrent;
using System.Text.Json;
using Watchpoint.Api.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Internal.Jobs;

/// <summary>In-memory job registry (spec API §5.6); finished jobs are dropped 24 h after they end.</summary>
internal sealed class JobRegistry(TimeProvider time) : IJobRegistry, IDisposable
{
    public static readonly TimeSpan Retention = TimeSpan.FromHours(24);

    private readonly ConcurrentDictionary<Guid, Entry> _jobs = new();

    public JobInfo Start(string kind, UserId owner, Func<IJobProgress, CancellationToken, Task<Result<JsonElement?>>> work)
    {
        ArgumentNullException.ThrowIfNull(work);
        Purge();
        var entry = new Entry(new JobInfo(Guid.CreateVersion7(), kind, owner, JobStatus.Running, 0, null, null, null, time.GetUtcNow(), null));
        _jobs[entry.Info.Id] = entry;
        entry.Task = Task.Run(() => RunAsync(entry, work));
        return entry.Info;
    }

    public JobInfo? Find(Guid id)
    {
        Purge();
        return _jobs.TryGetValue(id, out var entry) ? entry.Info : null;
    }

    public bool Cancel(Guid id)
    {
        if (!_jobs.TryGetValue(id, out var entry))
        {
            return false;
        }

        if (entry.Info.Status == JobStatus.Running)
        {
            entry.Cancellation.Cancel();
            entry.Update(i => i.Status == JobStatus.Running ? i with { Status = JobStatus.Cancelled, FinishedAt = time.GetUtcNow() } : i);
        }

        return true;
    }

    /// <summary>Waits for a job to finish (tests).</summary>
    internal Task WhenFinishedAsync(Guid id) => _jobs.TryGetValue(id, out var entry) && entry.Task is { } task ? task : Task.CompletedTask;

    public void Dispose()
    {
        foreach (var entry in _jobs.Values)
        {
            entry.Cancellation.Cancel();
            entry.Cancellation.Dispose();
        }
    }

    private async Task RunAsync(Entry entry, Func<IJobProgress, CancellationToken, Task<Result<JsonElement?>>> work)
    {
        try
        {
            var result = await work(entry, entry.Cancellation.Token).ConfigureAwait(false);
            entry.Update(i => i.Status != JobStatus.Running ? i : result.TryGetValue(out var value, out var error)
                ? i with { Status = JobStatus.Succeeded, Progress = 100, Result = value, FinishedAt = time.GetUtcNow() }
                : i with { Status = JobStatus.Failed, Error = error, FinishedAt = time.GetUtcNow() });
        }
        catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
        {
            entry.Update(i => i with { Status = JobStatus.Cancelled, FinishedAt = i.FinishedAt ?? time.GetUtcNow() });
        }
#pragma warning disable CA1031 // A failing job is recorded as failed; nothing else may observe the exception.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            entry.Update(i => i with { Status = JobStatus.Failed, Error = Error.Internal(ex.GetType().Name), FinishedAt = time.GetUtcNow() });
        }
    }

    private void Purge()
    {
        var threshold = time.GetUtcNow() - Retention;
        foreach (var (id, entry) in _jobs)
        {
            if (entry.Info.FinishedAt is { } finished && finished < threshold && _jobs.TryRemove(id, out _))
            {
                entry.Cancellation.Dispose();
            }
        }
    }

    private sealed class Entry(JobInfo info) : IJobProgress
    {
        private readonly Lock _lock = new();

        public JobInfo Info { get; private set; } = info;

        public CancellationTokenSource Cancellation { get; } = new();

        public Task? Task { get; set; }

        public void Report(int percent, string? messageKey = null) =>
            Update(i => i.Status == JobStatus.Running ? i with { Progress = Math.Clamp(percent, 0, 100), ProgressMessageKey = messageKey } : i);

        public void Update(Func<JobInfo, JobInfo> change)
        {
            lock (_lock)
            {
                Info = change(Info);
            }
        }
    }
}
