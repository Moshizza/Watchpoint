using System.Text;
using Shouldly;
using SortDirection = Watchpoint.Audit.Contracts.SortDirection;
using Watchpoint.Audit.Contracts;
using Watchpoint.Audit.Internal;
using Watchpoint.Kernel.Paging;
using Xunit;

namespace Watchpoint.Audit.Tests;

[Trait("layer", "U")]
[Trait("spec", "AUD")]
public sealed class AuditQueryTests
{
    private static readonly DateTimeOffset _t0 = new(2026, 3, 1, 8, 0, 0, TimeSpan.Zero);

    private static readonly Guid _bob = new("00000000-0000-7000-8000-0000000000b0");

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    [Fact]
    public async Task AUD_U_013_Query_filters_by_actor_action_prefix_object_outcome_severity_correlation_and_text()
    {
        await using var h = await SeedAsync();
        var query = new AuditQueryService(h.Store, new DefaultAuditLabelResolver());
        var all = new AuditQuery(_t0, _t0.AddDays(2));
        var corr = Guid.Parse("00000000-0000-7000-8000-0000000000c1");
        var other = Guid.Parse("00000000-0000-7000-8000-0000000000e1");

        async Task<List<string>> Run(AuditQuery q) => [.. (await query.QueryAsync(q, new PageRequest(100), Ct)).Value.Items.Select(e => e.Action.Value + "/" + e.Reason)];

        (await Run(all)).Count.ShouldBe(7);
        (await Run(all with { Actors = [_bob] })).ShouldBe(["rights.copyApplied/review", "rights.grantsChanged/-"], ignoreOrder: true);
        (await Run(all with { ActionPrefixes = ["rights."] })).Count.ShouldBe(2);
        (await Run(all with { ActionPrefixes = ["identity.signedIn", "alarms."] })).Count.ShouldBe(3);
        (await Run(all with { ObjectType = "user" })).Count.ShouldBe(2);
        (await Run(all with { ObjectId = AuditHarness.Door })).Count.ShouldBe(4);
        (await Run(all with { ObjectType = "device", ObjectId = other })).ShouldBeEmpty();
        (await Run(all with { Outcome = AuditOutcome.Denied })).ShouldBe(["rights.copyApplied/review"]);
        (await Run(all with { MinSeverity = AuditSeverity.Sensitive })).Count.ShouldBe(3);
        (await Run(all with { MinSeverity = AuditSeverity.Critical })).ShouldBe(["identity.breakGlassUsed/fire"]);
        (await Run(all with { CorrelationId = corr })).Count.ShouldBe(2);
        (await Run(all with { Text = "REVIEW" })).ShouldBe(["rights.copyApplied/review"]);
        (await Run(all with { From = _t0.AddHours(1), To = _t0.AddHours(3) })).Count.ShouldBe(2);

        // The query reaches the store as the typed filter (sort, outcome and severity as numbers).
        await query.QueryAsync(all with { Outcome = AuditOutcome.Failure, MinSeverity = AuditSeverity.Sensitive, Sort = SortDirection.Ascending }, new PageRequest(5), Ct);
        h.Store.LastFilter.ShouldNotBeNull().ShouldSatisfyAllConditions(
            f => f.Outcome.ShouldBe((short)1),
            f => f.MinSeverity.ShouldBe((short)1),
            f => f.Descending.ShouldBeFalse());
    }

    [Fact]
    public async Task AUD_U_014_Query_enforces_the_range_limit_and_pages_with_a_keyset_cursor()
    {
        await using var h = new AuditHarness();
        await h.StartAsync();

        // 25 records, several sharing a timestamp so the id breaks the tie.
        for (var i = 0; i < 25; i++)
        {
            await h.WriteAsync(AuditHarness.Record(at: _t0.AddMinutes(i / 3), severity: AuditSeverity.Critical));
        }

        var query = new AuditQueryService(h.Store, new DefaultAuditLabelResolver());
        var range = new AuditQuery(_t0, _t0.AddDays(1));

        foreach (var sort in new[] { SortDirection.Descending, SortDirection.Ascending })
        {
            List<long> seen = [];
            string? cursor = null;
            var pages = 0;
            do
            {
                var page = (await query.QueryAsync(range with { Sort = sort }, new PageRequest(10, cursor), Ct)).Value;
                seen.AddRange(page.Items.Select(e => e.Id));
                cursor = page.NextCursor;
                pages++;
            }
            while (cursor is not null);

            pages.ShouldBe(3);
            seen.Count.ShouldBe(25);
            seen.Distinct().Count().ShouldBe(25);
            (sort == SortDirection.Ascending ? seen.Order() : seen.OrderDescending()).ShouldBe(seen);
        }

        // Range limit: 366 days is allowed, more or an empty range is not.
        (await query.QueryAsync(new AuditQuery(_t0, _t0.AddDays(366)), new PageRequest(1), Ct)).IsSuccess.ShouldBeTrue();
        var tooLarge = await query.QueryAsync(new AuditQuery(_t0, _t0.AddDays(366).AddSeconds(1)), new PageRequest(1), Ct);
        tooLarge.Error.Code.Value.ShouldBe("audit.query.range_too_large");
        (await query.QueryAsync(new AuditQuery(_t0, _t0), new PageRequest(1), Ct)).Error.Code.Value.ShouldBe("audit.query.range_too_large");
        (await query.QueryAsync(range, new PageRequest(1, "not-a-cursor!"), Ct)).IsSuccess.ShouldBeFalse();
        (await query.GetAsync(1, Ct)).ShouldNotBeNull();
        (await query.GetAsync(9999, Ct)).ShouldBeNull();
    }

    [Fact]
    public async Task AUD_U_016_Personal_data_is_stored_as_ids_only_labels_are_resolved_at_query_time_and_an_erase_leaves_the_chain_valid()
    {
        await using var h = new AuditHarness();
        await h.StartAsync();
        var holder = Guid.Parse("00000000-0000-7000-8000-0000000000f1");
        await h.WriteAsync(AuditHarness.Record("privacy.subjectExported", AuditSeverity.Critical, objects: [new AuditObjectRef("person", holder, "Ann Example")]));

        // Nothing readable about the person is in the row.
        var row = h.Store.Rows.ShouldHaveSingleItem();
        row.ObjectsJson.ShouldNotContain("Ann");
        row.ObjectsJson.ShouldContain(holder.ToString("D"));
        row.ObjectIds.ShouldBe([holder]);

        var resolver = new MapResolver();
        var query = new AuditQueryService(h.Store, resolver);
        var range = new AuditQuery(_t0.AddYears(5), _t0.AddYears(5).AddDays(1));
        var now = new AuditQuery(h.Clock.GetUtcNow().AddMinutes(-1), h.Clock.GetUtcNow().AddMinutes(1));

        resolver.Labels[("person", holder)] = "Ann Example";
        (await query.QueryAsync(now, new PageRequest(10), Ct)).Value.Items.Single().Objects.Single().Label.ShouldBe("Ann Example");

        // Privacy erase: the resolver no longer knows the person; no audit row changes, the chain stays valid.
        resolver.Labels.Clear();
        (await query.QueryAsync(now, new PageRequest(10), Ct)).Value.Items.Single().Objects.Single().Label.ShouldBeNull();
        (await h.Verifier.VerifyAsync("node-1", 1, null, Ct)).Valid.ShouldBeTrue();
        _ = range;
    }

    [Fact]
    public async Task AUD_U_018_Export_streams_csv_with_localised_action_labels_and_is_itself_audited()
    {
        await using var h = new AuditHarness();
        await h.StartAsync();
        await h.WriteAsync(AuditHarness.Record("identity.signedIn", at: _t0, reason: "=HYPERLINK(\"x\")"));
        await h.WriteAsync(AuditHarness.Record("alarms.acknowledged", at: _t0.AddMinutes(1), reason: "line1\nline2, \"quoted\""));
        await h.Writer.StopAsync(Ct);
        var sink = new CapturingSink();
        var queries = new AuditQueryService(h.Store, new MapResolver());
        var exporter = new AuditExporter(queries, sink);
        using var output = new MemoryStream();
        var actor = AuditActor.User(AuditHarness.Alice, "Alice");

        var result = await exporter.ExportCsvAsync(new AuditQuery(_t0.AddHours(-1), _t0.AddHours(1), Sort: SortDirection.Ascending), output, "fr", actor, Ct);

        result.Value.ShouldBe(2);
        var lines = Encoding.UTF8.GetString(output.ToArray()).Split("\r\n", StringSplitOptions.None);
        lines[0].ShouldStartWith("occurredAt,recordedAt,chain,sequence,action,actionLabel");
        lines[1].ShouldContain("identity.signedIn,fr:identity.signedIn");
        lines[1].ShouldContain("'=HYPERLINK");
        lines[1].ShouldNotContain(",=HYPERLINK");
        lines[2].ShouldContain("fr:alarms.acknowledged");
        lines[2].ShouldContain("\"line1\nline2, \"\"quoted\"\"\"");

        var audit = sink.Records.ShouldHaveSingleItem();
        audit.Action.Value.ShouldBe("audit.exported");
        audit.Severity.ShouldBe(AuditSeverity.Sensitive);
        audit.Actor.ShouldBe(actor);

        // An invalid range fails before anything is written or audited.
        using var empty = new MemoryStream();
        var bad = await exporter.ExportCsvAsync(new AuditQuery(_t0, _t0.AddDays(400)), empty, "en", actor, Ct);
        bad.IsSuccess.ShouldBeFalse();
        empty.Length.ShouldBe(0);
        sink.Records.Count.ShouldBe(1);
    }

    private static async Task<AuditHarness> SeedAsync()
    {
        var h = new AuditHarness();
        await h.StartAsync();
        var corr = Guid.Parse("00000000-0000-7000-8000-0000000000c1");
        var user = Guid.Parse("00000000-0000-7000-8000-0000000000e2");
        var bobActor = AuditActor.User(_bob, "Bob");
        AuditRecord Make(string action, AuditActor actor, AuditOutcome outcome, AuditSeverity severity, int hours, string? reason = null, Guid? correlation = null, params AuditObjectRef[] objects) =>
            new(new AuditAction(action), outcome, actor, objects, reason, null, severity, correlation, _t0.AddHours(hours));

        var alice = AuditActor.User(AuditHarness.Alice, "Alice");
        var door = new AuditObjectRef("device", AuditHarness.Door);
        await h.WriteAsync(Make("identity.signedIn", alice, AuditOutcome.Success, AuditSeverity.Normal, 0, null, corr, door));
        await h.WriteAsync(Make("identity.signedIn", bobActor with { Id = Guid.CreateVersion7() }, AuditOutcome.Failure, AuditSeverity.Normal, 1, null, null, door));
        await h.WriteAsync(Make("alarms.acknowledged", alice, AuditOutcome.Success, AuditSeverity.Normal, 2, null, corr, door));
        await h.WriteAsync(Make("rights.copyApplied", bobActor, AuditOutcome.Denied, AuditSeverity.Sensitive, 3, "review", null, new AuditObjectRef("user", user)));
        await h.WriteAsync(Make("rights.grantsChanged", bobActor, AuditOutcome.Success, AuditSeverity.Sensitive, 4, "-", null, new AuditObjectRef("user", user)));
        await h.WriteAsync(Make("identity.breakGlassUsed", alice, AuditOutcome.Success, AuditSeverity.Critical, 5, "fire", null));
        await h.WriteAsync(Make("commands.requested", alice, AuditOutcome.Success, AuditSeverity.Normal, 6, null, null, door));
        await h.Writer.StopAsync(Ct);
        h.Store.Rows.Count.ShouldBe(7);
        return h;
    }

    private sealed class MapResolver : IAuditLabelResolver
    {
        public Dictionary<(string Type, Guid Id), string> Labels { get; } = [];

        public ValueTask<IReadOnlyDictionary<(string Type, Guid Id), string>> ResolveAsync(IReadOnlyCollection<(string Type, Guid Id)> objects, CancellationToken ct) =>
            ValueTask.FromResult<IReadOnlyDictionary<(string Type, Guid Id), string>>(Labels.Where(p => objects.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value));

        public string ActionLabel(AuditAction action, string language) => language + ":" + action.Value;
    }

    private sealed class CapturingSink : IAuditSink
    {
        public List<AuditRecord> Records { get; } = [];

        public ValueTask WriteAsync(AuditRecord record, CancellationToken ct = default)
        {
            Records.Add(record);
            return ValueTask.CompletedTask;
        }
    }
}
