using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Watchpoint.Configuration.Contracts;
using Watchpoint.Configuration.Internal.Drafts;
using Watchpoint.Configuration.Internal.Publish;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;

namespace Watchpoint.Configuration.Internal.Versions;

/// <summary>Versions, diff, objects at a version and rollback drafts (spec CFG §7.3).</summary>
internal sealed class VersionService(IPublishStore store, DraftService drafts, ILogger<VersionService> logger) : IVersionService
{
    public async Task<Page<ConfigVersion>> ListAsync(VersionQuery query, PageRequest page, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(query);
        ArgumentNullException.ThrowIfNull(page);
        var skip = page.Cursor is { } cursor && int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var offset) ? offset : 0;
        var items = await store.ListAsync(query, skip, page.Limit + 1, ct).ConfigureAwait(false);
        var more = items.Count > page.Limit;
        return new Page<ConfigVersion>([.. items.Take(page.Limit)], more ? (skip + page.Limit).ToString(CultureInfo.InvariantCulture) : null);
    }

    public async Task<Result<ConfigVersionDetail>> GetAsync(long version, CancellationToken ct) =>
        await store.GetAsync(version, ct).ConfigureAwait(false) is { } record
            ? new ConfigVersionDetail(record.Version, record.Entries, record.Diff)
            : NotFound(version);

    public async Task<Result<DraftId>> CreateRollbackDraftAsync(long version, UserId actor, bool restore, CancellationToken ct)
    {
        if (await store.GetAsync(version, ct).ConfigureAwait(false) is not { } record)
        {
            return NotFound(version);
        }

        IReadOnlyList<DraftEntry> entries = restore ? Restore(await store.AfterAsync(version, ct).ConfigureAwait(false)) : record.Inverse;
        var name = restore ? "Restore to version " + version.ToString(CultureInfo.InvariantCulture) : "Undo version " + version.ToString(CultureInfo.InvariantCulture);
        var draft = (await drafts.CreateCoreAsync(LocalizedName.Of(name), actor, hidden: false, ct).ConfigureAwait(false)).Value;
        foreach (var entry in entries)
        {
            var set = await drafts.SetEntryAsync(draft.Id, actor, draft.Version, new EntryRequest(entry.Ref, entry.Operation, entry.BaseVersion, entry.Content), ct)
                .ConfigureAwait(false);
            if (!set.TryGetValue(out var done, out var error))
            {
                await drafts.DiscardAsync(draft.Id, actor, ct).ConfigureAwait(false);
                return error;
            }

            draft = done.Draft;
        }

        ConfigurationLog.RollbackCreated(logger, draft.Id.Value, version);
        return draft.Id;
    }

    public async Task<Result<JsonElement>> GetObjectAtVersionAsync(ConfigObjectRef obj, long version, CancellationToken ct)
    {
        if (await store.GetAsync(version, ct).ConfigureAwait(false) is null)
        {
            return NotFound(version);
        }

        return await store.ObjectAtAsync(obj, version, ct).ConfigureAwait(false) is { After: { } document }
            ? document
            : Error.Create(ErrorKind.NotFound, ConfigurationErrors.VersionNotFound, ("version", version), ("type", obj.Type), ("id", obj.Id));
    }

    /// <summary>
    /// "Restore to version v": one entry per object changed after v, from the inverses of the later versions (newest
    /// first). The document is the state right after v (the oldest inverse); the base version is the live one (the newest).
    /// </summary>
    internal static List<DraftEntry> Restore(IReadOnlyList<VersionRecord> later)
    {
        var newest = new Dictionary<ConfigObjectRef, DraftEntry>();
        var oldest = new Dictionary<ConfigObjectRef, DraftEntry>();
        var order = new List<ConfigObjectRef>();
        foreach (var version in later.OrderByDescending(v => v.Version.Version))
        {
            foreach (var inverse in version.Inverse)
            {
                if (newest.TryAdd(inverse.Ref, inverse))
                {
                    order.Add(inverse.Ref);
                }

                oldest[inverse.Ref] = inverse;
            }
        }

        var entries = new List<DraftEntry>();
        foreach (var obj in order)
        {
            var existsNow = newest[obj].Operation != EntryOperation.Create;     // undoing a delete: it does not exist now
            var existedThen = oldest[obj].Operation != EntryOperation.Delete;   // undoing a create: it did not exist then
            var entry = (existsNow, existedThen) switch
            {
                (true, true) => new DraftEntry(Guid.CreateVersion7(), obj, EntryOperation.Update, newest[obj].BaseVersion, oldest[obj].Content, default),
                (true, false) => new DraftEntry(Guid.CreateVersion7(), obj, EntryOperation.Delete, newest[obj].BaseVersion, null, default),
                (false, true) => new DraftEntry(Guid.CreateVersion7(), obj, EntryOperation.Create, null, oldest[obj].Content, default),
                _ => null,
            };
            if (entry is not null)
            {
                entries.Add(entry);
            }
        }

        return entries;
    }

    private static Error NotFound(long version) => Error.Create(ErrorKind.NotFound, ConfigurationErrors.VersionNotFound, ("version", version));
}
