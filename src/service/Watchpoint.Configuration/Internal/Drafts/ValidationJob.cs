using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Configuration.Internal.Drafts;

/// <summary>A validation of a whole draft in the background, with progress (spec CFG §5: drafts too large to validate on every change).</summary>
internal sealed class ValidationJob : IValidationJob
{
    private int _done;

    public ValidationJob(DraftService service, DraftId id, UserId actor, CancellationToken ct)
    {
        Completion = RunAsync(service, id, actor, ct);
    }

    public int Total { get; private set; }

    public int Done => Volatile.Read(ref _done);

    public Task<Result<Draft>> Completion { get; }

    private async Task<Result<Draft>> RunAsync(DraftService service, DraftId id, UserId actor, CancellationToken ct)
    {
        await Task.Yield();
        if (!(await service.GetAsync(id, actor, ct).ConfigureAwait(false)).TryGetValue(out var draft, out var error))
        {
            return error;
        }

        Total = draft.Entries.Count;
        var problems = await service.ValidateAllAsync(draft.Entries, done => Volatile.Write(ref _done, done), ct).ConfigureAwait(false);
        return await service.StoreProblemsAsync(draft, problems, ct).ConfigureAwait(false);
    }
}
