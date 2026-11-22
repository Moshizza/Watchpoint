using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Configuration.Internal.Drafts;

/// <summary>Drafts in memory: compare-and-swap on the draft version, as the PostgreSQL store will do with <c>row_version</c>.</summary>
internal sealed class InMemoryDraftRepository : IDraftRepository
{
    private readonly Lock _gate = new();
    private readonly Dictionary<DraftId, Stored> _drafts = [];

    public Task AddAsync(Draft draft, bool hidden, CancellationToken ct)
    {
        lock (_gate)
        {
            _drafts.Add(draft.Id, new Stored(draft, hidden, []));
        }

        return Task.CompletedTask;
    }

    public Task<Draft?> GetAsync(DraftId id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_drafts.TryGetValue(id, out var stored) ? stored.Draft : null);
        }
    }

    public Task<bool> TryReplaceAsync(Draft draft, long expectedVersion, IReadOnlyList<DraftSecretRef>? secrets, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_drafts.TryGetValue(draft.Id, out var stored) || stored.Draft.Version != expectedVersion)
            {
                return Task.FromResult(false);
            }

            _drafts[draft.Id] = stored with { Draft = draft, Secrets = secrets ?? stored.Secrets };
            return Task.FromResult(true);
        }
    }

    public Task<bool> TrySetProblemsAsync(DraftId id, long version, IReadOnlyList<Problem> problems, CancellationToken ct)
    {
        lock (_gate)
        {
            if (!_drafts.TryGetValue(id, out var stored) || stored.Draft.Version != version)
            {
                return Task.FromResult(false);
            }

            _drafts[id] = stored with { Draft = stored.Draft with { Problems = problems } };
            return Task.FromResult(true);
        }
    }

    public Task<IReadOnlyList<Draft>> ListAsync(UserId user, bool ownedOnly, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult<IReadOnlyList<Draft>>([.. _drafts.Values
                .Where(s => !s.Hidden && s.Draft.Status != DraftStatus.Discarded && (s.Draft.Owner == user || (!ownedOnly && s.Draft.Collaborators.Contains(user))))
                .Select(s => s.Draft)
                .OrderByDescending(d => d.CreatedAt)
                .ThenByDescending(d => d.Id)]);
        }
    }

    public Task<IReadOnlyList<DraftSecretRef>> GetSecretsAsync(DraftId id, CancellationToken ct)
    {
        lock (_gate)
        {
            return Task.FromResult(_drafts.TryGetValue(id, out var stored) ? stored.Secrets : []);
        }
    }

    private sealed record Stored(Draft Draft, bool Hidden, IReadOnlyList<DraftSecretRef> Secrets);
}
