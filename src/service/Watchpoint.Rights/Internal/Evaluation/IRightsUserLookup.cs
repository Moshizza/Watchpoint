using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;

namespace Watchpoint.Rights.Internal.Evaluation;

/// <summary>The facts about a user the evaluator needs (in memory); adapted from the IDN directory.</summary>
internal interface IRightsUserLookup
{
    event EventHandler<UserId>? UserChanged;

    RightsUser? Find(UserId id);
}

/// <summary>The user facts used by the evaluator.</summary>
/// <param name="Kind">The user kind.</param>
/// <param name="Enabled">Enabled flag.</param>
/// <param name="Administrator">Administrator flag.</param>
/// <param name="ValidFrom">Start of validity.</param>
/// <param name="ValidUntil">End of validity.</param>
internal sealed record RightsUser(UserKind Kind, bool Enabled, bool Administrator, DateTimeOffset? ValidFrom, DateTimeOffset? ValidUntil)
{
    public bool CanHoldRights(DateTimeOffset now) =>
        Kind != UserKind.Template && Enabled && (ValidFrom is null || ValidFrom <= now) && (ValidUntil is null || now < ValidUntil);
}

/// <summary>Adapter over <see cref="IUserDirectory"/>.</summary>
internal sealed class DirectoryUserLookup : IRightsUserLookup
{
    private readonly IUserDirectory _directory;

    public DirectoryUserLookup(IUserDirectory directory)
    {
        _directory = directory;
        _directory.Changed += (_, e) => UserChanged?.Invoke(this, e.UserId);
    }

    public event EventHandler<UserId>? UserChanged;

    public RightsUser? Find(UserId id) =>
        _directory.Get(id).TryGetValue(out var u, out _) ? new RightsUser(u.Kind, u.Enabled, u.Administrator, u.ValidFrom, u.ValidUntil) : null;
}
