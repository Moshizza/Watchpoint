using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Registry.Contracts;
using Watchpoint.Registry.Internal;
using Xunit;

namespace Watchpoint.Registry.Tests;

/// <summary>The <c>registry.changed</c> record must fit the log's record limit, checked before commit (spec REG §6, §9; T1.09c).</summary>
public sealed partial class RegistryUnitTests
{
    [Fact]
    public async Task REG_U_052_Batch_whose_record_exceeds_the_log_limit_is_refused_before_commit()
    {
        var limit = new MeasuringLimit(64 * 1024);
        using var writer = WriterWith(limit);

        // 2,000 tags: far below the change-count limit, far above 64 KB once described in the record.
        var family = new TagFamily(TagFamilyId.New(), "bulk", new LocalizedName("Bulk", new Dictionary<string, string>()), null, TagCardinality.ZeroOrMore, 0);
        var changes = new List<RegistryChange> { new RegistryChange.UpsertTagFamily(family) };
        for (var i = 0; i < 2_000; i++)
        {
            changes.Add(new RegistryChange.UpsertTag(new Tag(TagId.New(), family.Id, $"t{i}", new LocalizedName($"T{i}", new Dictionary<string, string>()), null, 0)));
        }

        var result = await writer.ApplyAsync(new RegistryChangeBatch(changes), _context, Ct);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.Value.ShouldBe("registry.batch.too_large");
        result.Error.Args["max"].ShouldBe(64 * 1024);
        ((int)result.Error.Args["bytes"]!).ShouldBeGreaterThan(64 * 1024);
        limit.Measured.ShouldHaveSingleItem().Revision.ShouldBe(1); // measured inside the transaction, with the revision it would take

        // Nothing was written: no row, no revision, no outbox row; the snapshot is unchanged.
        await using var db = _factory.CreateDbContext();
        db.Tags.ShouldBeEmpty();
        db.TagFamilies.ShouldBeEmpty();
        db.Revision.ShouldBeEmpty();
        db.Outbox.ShouldBeEmpty();
        _reader.GetTags().ShouldBeEmpty();

        // The next batch takes revision 1.
        (await writer.ApplyAsync(SiteBatch("UTC"), _context, Ct)).IsSuccess.ShouldBeTrue();
        (await _outbox.ReadUnsentAsync(10, Ct)).Select(r => r.Revision).ShouldBe([1L]);
    }

    [Fact]
    public async Task REG_U_068_Record_exactly_at_the_log_limit_is_committed_one_byte_over_is_refused()
    {
        // The exact size of the batch's record (revision 1, the test clock's commit time, this context).
        var probe = new MeasuringLimit(int.MaxValue);
        var size = probe.Measure(new RegistryOutboxRecord(1, _clock.GetUtcNow(), _context, [new EntityChange("site", _reader.Site.Id.Value, ChangeKind.Updated)]));

        var over = new MeasuringLimit(size - 1);
        using (var writer = WriterWith(over))
        {
            var refused = await writer.ApplyAsync(SiteBatch("UTC"), _context, Ct);
            refused.Error.Code.Value.ShouldBe("registry.batch.too_large");
            refused.Error.Args["max"].ShouldBe(size - 1);
            refused.Error.Args["bytes"].ShouldBe(size);
        }

        (await _outbox.ReadUnsentAsync(10, Ct)).ShouldBeEmpty();

        var exact = new MeasuringLimit(size);
        using (var writer = WriterWith(exact))
        {
            (await writer.ApplyAsync(SiteBatch("UTC"), _context, Ct)).IsSuccess.ShouldBeTrue();
        }

        var measured = exact.Measured.ShouldHaveSingleItem();
        measured.Revision.ShouldBe(1);
        measured.Context.ShouldBe(_context);
        measured.Changes.ShouldBe([new EntityChange("site", _reader.Site.Id.Value, ChangeKind.Updated)]);
        (await _outbox.ReadUnsentAsync(10, Ct)).Select(r => r.Revision).ShouldBe([1L]);
    }

    private RegistryWriter WriterWith(IRegistryRecordLimit limit) =>
        new(_factory, _types, _outbox, _clock, NullLogger<RegistryWriter>.Instance, s => _snapshot = s, () => _snapshot, _enrichmentCache, _feed, limit);

    /// <summary>A deterministic stand-in for the pipeline's measure: the UTF-8 length of the record as JSON.</summary>
    private sealed class MeasuringLimit(int maxBytes) : IRegistryRecordLimit
    {
        public List<RegistryOutboxRecord> Measured { get; } = [];

        public int MaxBytes => maxBytes;

        public int Measure(RegistryOutboxRecord record)
        {
            Measured.Add(record);
            var changes = string.Join(',', record.Changes.Select(c => $"{{\"entity\":\"{c.Entity}\",\"id\":\"{c.Id}\",\"kind\":\"{c.Kind}\"}}"));
            var json = $"{{\"revision\":{record.Revision},\"ts\":{JsonSerializer.Serialize(record.CreatedAt)},\"origin\":\"{record.Context.Origin}\",\"changes\":[{changes}]}}";
            return Encoding.UTF8.GetByteCount(json);
        }
    }
}
