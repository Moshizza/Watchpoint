using System.Text.Json;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Api.Contracts;

/// <summary>Status of a long-running operation (spec API §5.6).</summary>
public enum JobStatus
{
    /// <summary>Running.</summary>
    Running,

    /// <summary>Finished successfully; <see cref="JobInfo.Result"/> holds the result.</summary>
    Succeeded,

    /// <summary>Finished with an error; <see cref="JobInfo.Error"/> holds it.</summary>
    Failed,

    /// <summary>Cancelled by its owner or an administrator.</summary>
    Cancelled,
}

/// <summary>A snapshot of a job.</summary>
/// <param name="Id">The job id.</param>
/// <param name="Kind">What the job does, e.g. <c>irt.discovery</c>.</param>
/// <param name="Owner">The user who started it.</param>
/// <param name="Status">The status.</param>
/// <param name="Progress">Progress 0–100.</param>
/// <param name="ProgressMessageKey">The progress message key, if any.</param>
/// <param name="Result">The result (JSON) when succeeded.</param>
/// <param name="Error">The error when failed.</param>
/// <param name="StartedAt">Start time.</param>
/// <param name="FinishedAt">End time, if finished.</param>
public sealed record JobInfo(Guid Id, string Kind, UserId Owner, JobStatus Status, int Progress, string? ProgressMessageKey,
    JsonElement? Result, Error? Error, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt);

/// <summary>Reports a job's progress.</summary>
public interface IJobProgress
{
    /// <summary>Sets the progress.</summary>
    /// <param name="percent">0–100 (clamped).</param>
    /// <param name="messageKey">The message key, if any.</param>
    void Report(int percent, string? messageKey = null);
}

/// <summary>Registry of long-running operations (spec API §5.6): retained 24 h after they finish.</summary>
public interface IJobRegistry
{
    /// <summary>Starts <paramref name="work"/> in the background and returns the running job.</summary>
    /// <param name="kind">What the job does.</param>
    /// <param name="owner">The user who starts it.</param>
    /// <param name="work">The work; its result becomes the job result.</param>
    /// <returns>The job.</returns>
    JobInfo Start(string kind, UserId owner, Func<IJobProgress, CancellationToken, Task<Result<JsonElement?>>> work);

    /// <summary>The job, if it exists and has not expired.</summary>
    /// <param name="id">The job id.</param>
    /// <returns>The job or <see langword="null"/>.</returns>
    JobInfo? Find(Guid id);

    /// <summary>Requests cancellation of a running job.</summary>
    /// <param name="id">The job id.</param>
    /// <returns><see langword="true"/> when the job existed.</returns>
    bool Cancel(Guid id);
}
