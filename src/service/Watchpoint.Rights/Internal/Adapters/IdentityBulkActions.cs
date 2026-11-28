using Watchpoint.Identity.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Results;
using Watchpoint.Rights.Contracts;

namespace Watchpoint.Rights.Internal.Adapters;

/// <summary>Group bulk actions executed through IDN (spec RGT §7).</summary>
internal sealed class IdentityBulkActions(IUserDirectory users, ISessionManagement sessions) : IUserBulkActions
{
    public async Task<Result> RunAsync(UserId user, GroupBulkAction action, DateTimeOffset? parameter, UserId actor, CancellationToken ct)
    {
        var context = new ChangeContext(actor, "rights.group");
        switch (action)
        {
            case GroupBulkAction.Disable:
            case GroupBulkAction.ExtendValidity:
                if (!users.Get(user).TryGetValue(out var current, out var error))
                {
                    return error;
                }

                var update = action == GroupBulkAction.Disable ? new UserUpdate(Enabled: false) : new UserUpdate(ValidUntil: parameter);
                return await users.UpdateAsync(user, update, current.Version, context, ct).ConfigureAwait(false);
            case GroupBulkAction.SignOut:
                var list = await sessions.ListAsync(actor, user, ct).ConfigureAwait(false);
                if (!list.TryGetValue(out var open, out var listError))
                {
                    return listError;
                }

                foreach (var s in open)
                {
                    var revoked = await sessions.RevokeAsync(actor, s.Id, "rights.group.bulk", ct).ConfigureAwait(false);
                    if (!revoked.IsSuccess)
                    {
                        return revoked;
                    }
                }

                return Result.Success();
            default:
                return Error.Create(ErrorKind.Unprocessable, "rights.group.bulk_not_supported", ("action", action.ToString()));
        }
    }
}
