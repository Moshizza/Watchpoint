using Microsoft.EntityFrameworkCore;
using Watchpoint.Kernel.Ids;
using Watchpoint.Rights.Contracts;
using Watchpoint.Rights.Internal.Evaluation;

namespace Watchpoint.Rights.Internal.Storage;

/// <summary>EF Core persistence of grants and groups (<c>wp_rights</c>, spec RGT §11).</summary>
internal sealed class EfRightsPersistence(IDbContextFactory<RightsDbContext> factory) : IRightsPersistence
{
    public async Task<IReadOnlyList<RightGrant>> LoadGrantsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Grants.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        return [.. rows.Select(r => new RightGrant(RightGrantId.From(r.Id), UserId.From(r.UserId), PermissionCode.FromTrusted(r.Permission),
            RightsJson.Scope(r.Scope), r.ScheduleCalendar, RightsJson.Constraints(r.Constraints), RightsJson.Source(r.Source), r.CreatedAt, UserId.From(r.CreatedBy)))];
    }

    public async Task SaveGrantsAsync(UserId user, IReadOnlyList<RightGrant> grants, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var old = await db.Grants.Where(g => g.UserId == user.Value).ToListAsync(ct).ConfigureAwait(false);
        db.Grants.RemoveRange(old);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.Grants.AddRange(grants.Select(g => new GrantRecord
        {
            Id = g.Id.Value,
            UserId = user.Value,
            Permission = g.Permission.Value,
            Scope = RightsJson.Scope(g.Scope),
            ScheduleCalendar = g.ScheduleCalendar,
            Constraints = RightsJson.Constraints(g.Constraints),
            Source = RightsJson.Source(g.Source),
            IdentityHash = GrantIdentity.Hash(g),
            CreatedAt = g.CreatedAt,
            CreatedBy = g.CreatedBy.Value,
        }));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserGroup>> LoadGroupsAsync(CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var groups = await db.UserGroups.AsNoTracking().ToListAsync(ct).ConfigureAwait(false);
        var members = (await db.UserGroupMembers.AsNoTracking().ToListAsync(ct).ConfigureAwait(false)).ToLookup(m => m.GroupId);
        return [.. groups.Select(g => new UserGroup(UserGroupId.From(g.Id), RightsJson.Name(g.Name), g.Description,
            [.. members[g.Id].Select(m => UserId.From(m.UserId))], [.. RightsJson.Guids(g.Managers).Select(UserId.From)], g.Version))];
    }

    public async Task SaveGroupAsync(UserGroup group, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var tx = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = await db.UserGroups.FindAsync([group.Id.Value], ct).ConfigureAwait(false);
        if (row is null)
        {
            row = new UserGroupRecord { Id = group.Id.Value };
            db.UserGroups.Add(row);
        }

        row.Name = RightsJson.Name(group.Name);
        row.Description = group.Description;
        row.Managers = RightsJson.Guids(group.Managers.Select(m => m.Value));
        row.Version = group.Version;
        db.UserGroupMembers.RemoveRange(db.UserGroupMembers.Where(m => m.GroupId == group.Id.Value));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        db.UserGroupMembers.AddRange(group.Members.Select(m => new UserGroupMemberRecord { GroupId = group.Id.Value, UserId = m.Value }));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await tx.CommitAsync(ct).ConfigureAwait(false);
    }

    public async Task DeleteGroupAsync(UserGroupId id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.UserGroupMembers.Where(m => m.GroupId == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.UserGroups.Where(g => g.Id == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
    }
}
