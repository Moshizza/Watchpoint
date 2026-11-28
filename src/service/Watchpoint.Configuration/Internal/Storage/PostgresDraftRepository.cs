using Microsoft.EntityFrameworkCore;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Kernel.Ids;
using Watchpoint.Storage.Contracts;

namespace Watchpoint.Configuration.Internal.Storage;

/// <summary>
/// Drafts in <c>wp_config</c> (spec CFG §11): a draft row with its entries, problems and vault references. A change
/// replaces the draft only if its version is still the expected one (compare and swap, one transaction).
/// </summary>
internal sealed class PostgresDraftRepository(IDbContextFactory<ConfigDbContext> factory) : IDraftRepository
{
    public async Task AddAsync(Draft draft, bool hidden, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        db.Drafts.Add(Row(draft, hidden));
        db.DraftEntries.AddRange(EntryRows(draft));
        db.DraftProblems.AddRange(ProblemRows(draft.Id, draft.Problems));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
    }

    public async Task<Draft?> GetAsync(DraftId id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return await db.Drafts.AsNoTracking().FirstOrDefaultAsync(d => d.Id == id.Value, ct).ConfigureAwait(false) is { } row
            ? await LoadAsync(db, row, ct).ConfigureAwait(false)
            : null;
    }

    public async Task<bool> TryReplaceAsync(Draft draft, long expectedVersion, IReadOnlyList<DraftSecretRef>? secrets, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(draft);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);
        var row = Row(draft, hidden: false);
        var updated = await db.Drafts.Where(d => d.Id == row.Id && d.Version == expectedVersion).ExecuteUpdateAsync(s => s
            .SetProperty(d => d.Name, row.Name)
            .SetProperty(d => d.Collaborators, row.Collaborators)
            .SetProperty(d => d.Status, row.Status)
            .SetProperty(d => d.PublishAt, row.PublishAt)
            .SetProperty(d => d.Approval, row.Approval)
            .SetProperty(d => d.UpdatedAt, row.UpdatedAt)
            .SetProperty(d => d.Version, row.Version), ct).ConfigureAwait(false);
        if (updated == 0)
        {
            return false;
        }

        await db.DraftEntries.Where(e => e.DraftId == row.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        await db.DraftProblems.Where(p => p.DraftId == row.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.DraftEntries.AddRange(EntryRows(draft));
        db.DraftProblems.AddRange(ProblemRows(draft.Id, draft.Problems));
        if (secrets is not null)
        {
            await db.DraftSecrets.Where(s => s.DraftId == row.Id).ExecuteDeleteAsync(ct).ConfigureAwait(false);
            db.DraftSecrets.AddRange(secrets.Select(s => new DraftSecretRow { DraftId = row.Id, EntryId = s.EntryId, FieldPath = s.FieldPath, SecretId = s.Secret.Value }));
        }

        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> TrySetProblemsAsync(DraftId id, long version, IReadOnlyList<Problem> problems, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(problems);
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await using var transaction = await db.Database.BeginTransactionAsync(ct).ConfigureAwait(false);

        // Locks the row and checks the version; the version itself does not change.
        if (await db.Drafts.Where(d => d.Id == id.Value && d.Version == version).ExecuteUpdateAsync(s => s.SetProperty(d => d.Version, version), ct).ConfigureAwait(false) == 0)
        {
            return false;
        }

        await db.DraftProblems.Where(p => p.DraftId == id.Value).ExecuteDeleteAsync(ct).ConfigureAwait(false);
        db.DraftProblems.AddRange(ProblemRows(id, problems));
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        await transaction.CommitAsync(ct).ConfigureAwait(false);
        return true;
    }

    public async Task<IReadOnlyList<Draft>> ListAsync(UserId user, bool ownedOnly, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var discarded = DraftStatus.Discarded.ToString();
        var rows = await db.Drafts.AsNoTracking()
            .Where(d => !d.Hidden && d.Status != discarded && (d.Owner == user.Value || (!ownedOnly && d.Collaborators.Contains(user.Value))))
            .OrderBy(d => d.CreatedAt)
            .ToListAsync(ct).ConfigureAwait(false);
        var drafts = new List<Draft>(rows.Count);
        foreach (var row in rows)
        {
            drafts.Add(await LoadAsync(db, row, ct).ConfigureAwait(false));
        }

        return drafts;
    }

    public async Task<IReadOnlyList<DraftSecretRef>> GetSecretsAsync(DraftId id, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        return [.. (await db.DraftSecrets.AsNoTracking().Where(s => s.DraftId == id.Value).ToListAsync(ct).ConfigureAwait(false))
            .Select(s => new DraftSecretRef(s.EntryId, s.FieldPath, SecretId.From(s.SecretId)))];
    }

    public async Task<IReadOnlyList<Draft>> ListByStatusAsync(DraftStatus status, CancellationToken ct)
    {
        await using var db = await factory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var text = status.ToString();
        var rows = await db.Drafts.AsNoTracking().Where(d => d.Status == text).OrderBy(d => d.CreatedAt).ToListAsync(ct).ConfigureAwait(false);
        var drafts = new List<Draft>(rows.Count);
        foreach (var row in rows)
        {
            drafts.Add(await LoadAsync(db, row, ct).ConfigureAwait(false));
        }

        return drafts;
    }

    private static async Task<Draft> LoadAsync(ConfigDbContext db, DraftRow row, CancellationToken ct)
    {
        var entries = await db.DraftEntries.AsNoTracking().Where(e => e.DraftId == row.Id).OrderBy(e => e.Position).ToListAsync(ct).ConfigureAwait(false);
        var problems = await db.DraftProblems.AsNoTracking().Where(p => p.DraftId == row.Id).OrderBy(p => p.Position).ToListAsync(ct).ConfigureAwait(false);
        return new Draft(
            DraftId.From(row.Id),
            LocalizedNameJsonConverter.FromJson(row.Name),
            UserId.From(row.Owner),
            [.. row.Collaborators.Select(UserId.From)],
            Enum.Parse<DraftStatus>(row.Status),
            [.. entries.Select(e => new DraftEntry(e.Id, new ConfigObjectRef(e.Type, e.ObjectId), Enum.Parse<EntryOperation>(e.Operation), e.BaseVersion,
                ConfigJson.Document(e.Content), e.UpdatedAt))],
            [.. problems.Select(p => new Problem(Enum.Parse<ProblemSeverity>(p.Severity), p.Code, ConfigJson.Args(p.Args),
                p.ObjectType is { } type && p.ObjectId is { } objectId ? new ConfigObjectRef(type, objectId) : null, p.Path, ConfigJson.ReadProblemFixes(p.Fixes)))],
            row.CreatedAt,
            row.UpdatedAt,
            row.PublishAt,
            row.Approval is { } approval ? ConfigJson.Read<ApprovalInfo>(approval) : null,
            row.Version);
    }

    private static DraftRow Row(Draft draft, bool hidden) => new()
    {
        Id = draft.Id.Value,
        Name = LocalizedNameJsonConverter.ToJson(draft.Name),
        Owner = draft.Owner.Value,
        Collaborators = [.. draft.Collaborators.Select(c => c.Value)],
        Status = draft.Status.ToString(),
        PublishAt = draft.PublishAt,
        Approval = draft.Approval is { } approval ? ConfigJson.Write(approval) : null,
        Hidden = hidden,
        CreatedAt = draft.CreatedAt,
        UpdatedAt = draft.UpdatedAt,
        Version = draft.Version,
    };

    private static IEnumerable<DraftEntryRow> EntryRows(Draft draft) => draft.Entries.Select((e, i) => new DraftEntryRow
    {
        Id = e.EntryId,
        DraftId = draft.Id.Value,
        Position = i,
        Type = e.Ref.Type,
        ObjectId = e.Ref.Id,
        Operation = e.Operation.ToString(),
        BaseVersion = e.BaseVersion,
        Content = ConfigJson.Document(e.Content),
        UpdatedAt = e.UpdatedAt,
    });

    private static IEnumerable<DraftProblemRow> ProblemRows(DraftId draft, IReadOnlyList<Problem> problems) => problems.Select((p, i) => new DraftProblemRow
    {
        DraftId = draft.Value,
        Position = i,
        Severity = p.Severity.ToString(),
        Code = p.Code,
        Args = ConfigJson.Write(p.Args),
        ObjectType = p.Object?.Type,
        ObjectId = p.Object?.Id,
        Path = p.Path,
        Fixes = ConfigJson.WriteProblemFixes(p.Fixes),
    });
}
