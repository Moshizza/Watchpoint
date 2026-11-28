using System.Collections.Concurrent;
using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Storage;

/// <summary>Holds the grants of all users in memory, loaded once at start and written through to persistence.</summary>
internal sealed class GrantStore(IRightsPersistence persistence) : IGrantStore
{
    private readonly ConcurrentDictionary<UserId, IReadOnlyList<RightGrant>> _byUser = new();

    public event EventHandler<GrantsChangedEventArgs>? Changed;

    public async Task LoadAsync(CancellationToken ct)
    {
        _byUser.Clear();
        foreach (var group in (await persistence.LoadGrantsAsync(ct).ConfigureAwait(false)).GroupBy(g => g.User))
        {
            _byUser[group.Key] = [.. group];
        }
    }

    public IReadOnlyList<RightGrant> GetGrants(UserId user) => _byUser.TryGetValue(user, out var g) ? g : [];

    public async Task ReplaceAsync(UserId user, IReadOnlyList<RightGrant> grants, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(grants);
        await persistence.SaveGrantsAsync(user, grants, ct).ConfigureAwait(false);
        _byUser[user] = grants;
        Changed?.Invoke(this, new GrantsChangedEventArgs([user]));
    }
}
