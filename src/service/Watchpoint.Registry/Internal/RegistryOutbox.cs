using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Channels;
using Microsoft.EntityFrameworkCore;
using Watchpoint.Kernel.Ids;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal.Storage;

namespace Watchpoint.Registry.Internal;

/// <summary>
/// The outbox table of the registry (spec REG §10, table <c>outbox</c>). The writer adds the rows inside its transaction
/// (<see cref="Add"/>, <see cref="Signal"/>); the pipeline's publisher uses the <see cref="IRegistryOutbox"/> contract.
/// </summary>
internal sealed class RegistryOutbox : IRegistryOutbox
{
    private static readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    private readonly IDbContextFactory<RegistryDbContext> _dbFactory;
    private readonly TimeProvider _clock;
    private readonly Channel<bool> _signal = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });

    public RegistryOutbox(IDbContextFactory<RegistryDbContext> dbFactory, TimeProvider clock)
    {
        _dbFactory = dbFactory;
        _clock = clock;
    }

    /// <summary>Adds the outbox row of a batch to the writer's context: saved and committed with the batch.</summary>
    public static void Add(RegistryDbContext db, long revision, DateTimeOffset createdAt, ChangeContext context, IReadOnlyList<EntityChange> changes)
    {
        db.Outbox.Add(new OutboxRecord
        {
            Revision = revision,
            CreatedAt = createdAt,
            Actor = context.Actor?.Value,
            Origin = context.Origin,
            ChangeSet = context.ChangeSet?.Value,
            Comment = context.Comment,
            Changes = JsonSerializer.Serialize(changes.Select(c => new StoredChange(c.Entity, c.Id, c.Kind)).ToList(), _json),
        });
    }

    /// <summary>Wakes a pending <see cref="WaitForRecordsAsync"/>; called by the writer after its transaction committed.</summary>
    public void Signal() => _signal.Writer.TryWrite(true);

    public async Task<IReadOnlyList<RegistryOutboxRecord>> ReadUnsentAsync(int limit, CancellationToken ct)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(limit, 1);
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        var rows = await db.Outbox.AsNoTracking()
            .Where(o => o.SentAt == null)
            .OrderBy(o => o.Revision)
            .Take(limit)
            .ToListAsync(ct)
            .ConfigureAwait(false);
        return rows.Select(ToRecord).ToList();
    }

    public async Task MarkSentAsync(long revision, CancellationToken ct)
    {
        var now = _clock.GetUtcNow();
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);
        await db.Outbox
            .Where(o => o.Revision == revision && o.SentAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(o => o.SentAt, now), ct)
            .ConfigureAwait(false);
    }

    public async Task<int> DeleteSentBeforeAsync(DateTimeOffset threshold, CancellationToken ct)
    {
        await using var db = await _dbFactory.CreateDbContextAsync(ct).ConfigureAwait(false);

        // The comparison runs on the client: SQLite (unit tests) cannot compare DateTimeOffset columns, and the sent set is
        // bounded by the retention (a day of registry changes).
        var sent = await db.Outbox.AsNoTracking()
            .Where(o => o.SentAt != null)
            .Select(o => new { o.Revision, o.SentAt })
            .ToListAsync(ct)
            .ConfigureAwait(false);
        var expired = sent.Where(o => o.SentAt!.Value < threshold).Select(o => o.Revision).ToList();
        if (expired.Count == 0)
        {
            return 0;
        }

        return await db.Outbox
            .Where(o => expired.Contains(o.Revision) && o.SentAt != null)
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);
    }

    public async Task WaitForRecordsAsync(CancellationToken ct) =>
        await _signal.Reader.ReadAsync(ct).ConfigureAwait(false);

    private static RegistryOutboxRecord ToRecord(OutboxRecord row)
    {
        var changes = JsonSerializer.Deserialize<List<StoredChange>>(row.Changes, _json) ?? [];
        return new RegistryOutboxRecord(
            row.Revision,
            row.CreatedAt,
            new ChangeContext(row.Actor.HasValue ? UserId.From(row.Actor.Value) : null, row.Origin, row.ChangeSet.HasValue ? ChangeSetId.From(row.ChangeSet.Value) : null, row.Comment),
            changes.Select(c => new EntityChange(c.Entity, c.Id, c.Kind)).ToList());
    }

    /// <summary>The JSON shape of one change in the <c>changes</c> column: <c>{ "entity", "id", "kind" }</c>, like the log record body.</summary>
    private sealed record StoredChange(string Entity, Guid Id, ChangeKind Kind);
}
