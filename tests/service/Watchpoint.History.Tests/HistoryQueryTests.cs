using System.Text.Json;
using Shouldly;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal.Queries;
using Watchpoint.History.Tests.Fakes;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Paging;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Text;
using Watchpoint.Registry.Contracts;
using Watchpoint.Storage.Contracts;
using Xunit;

namespace Watchpoint.History.Tests;

[Trait("layer", "U")]
[Trait("spec", "HIS")]
public sealed class HistoryQueryTests
{
    private static readonly DateTimeOffset _from = new(2026, 1, 5, 0, 0, 0, TimeSpan.Zero);

    private readonly FakeHistoryStore _store = new();
    private readonly FakeRegistryReader _registry = new();
    private readonly HistorySettings _settings = new();

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private HistoryQueries Queries() => new(_store, _registry, _settings);

    private static EventQuery Events(HistoryScope? scope = null, bool descending = true) =>
        new(_from, _from.AddDays(1), Scope: scope, Descending: descending);

    [Fact]
    public async Task HIS_U_017_Scope_filter_uses_the_smaller_of_locations_and_devices()
    {
        var l = (long n) => Watchpoint.Testing.Ids.TestIds.Location(n);
        var d = (long n) => Watchpoint.Testing.Ids.TestIds.Device(n);

        ScopeSelector.Select(null, out var nothing).ShouldBe(HistoryScopeClause.Unrestricted);
        nothing.ShouldBeFalse();
        ScopeSelector.Select(HistoryScope.Unrestricted, out _).ShouldBe(HistoryScopeClause.Unrestricted);

        var byLocations = ScopeSelector.Select(new HistoryScope([l(1), l(2)], null), out nothing);
        byLocations.LocationIds.ShouldBe([l(1).Value, l(2).Value]);
        byLocations.EntityIds.ShouldBeNull();

        var byDevices = ScopeSelector.Select(new HistoryScope(null, [d(1), d(2), d(3)]), out _);
        byDevices.EntityIds.ShouldBe([d(1).Value, d(2).Value, d(3).Value]);
        byDevices.LocationIds.ShouldBeNull();

        // Both known: the shorter list wins, locations on a tie.
        ScopeSelector.Select(new HistoryScope([l(1)], [d(1), d(2)]), out _).LocationIds.ShouldNotBeNull();
        ScopeSelector.Select(new HistoryScope([l(1), l(2), l(3)], [d(1), d(2)]), out _).EntityIds.ShouldNotBeNull();
        ScopeSelector.Select(new HistoryScope([l(1), l(2)], [d(1), d(2)]), out _).LocationIds.ShouldNotBeNull();

        // Nothing visible: no query is sent and the page is empty; the clause never turns into "no restriction".
        ScopeSelector.Select(new HistoryScope([], null), out nothing);
        nothing.ShouldBeTrue();
        var empty = (await Queries().QueryEventsAsync(Events(new HistoryScope(null, [])), new PageRequest(10), Ct)).ShouldBeSuccess();
        empty.Items.ShouldBeEmpty();
        empty.NextCursor.ShouldBeNull();
        _store.EventFilters.ShouldBeEmpty();

        // The chosen restriction reaches the store, in place of the caller's other list.
        (await Queries().QueryEventsAsync(Events(new HistoryScope([l(1)], [d(1), d(2)])), new PageRequest(10), Ct)).ShouldBeSuccess();
        _store.EventFilters.ShouldHaveSingleItem().Scope.LocationIds.ShouldBe([l(1).Value]);

        // Tags are looked up by key; tags that do not exist select nothing.
        var tag = new Tag(Watchpoint.Testing.Ids.TestIds.Tag(1), Watchpoint.Testing.Ids.TestIds.TagFamily(1), "entrance", LocalizedName.Of("Entrance"), null, 1);
        _registry.Tags.Add(tag);
        (await Queries().QueryEventsAsync(Events() with { Tags = [tag.Id] }, new PageRequest(10), Ct)).ShouldBeSuccess();
        _store.EventFilters.Last().TagKeys.ShouldBe(["entrance"]);
        var none = (await Queries().QueryEventsAsync(Events() with { Tags = [Watchpoint.Testing.Ids.TestIds.Tag(9)] }, new PageRequest(10), Ct)).ShouldBeSuccess();
        none.Items.ShouldBeEmpty();
        _store.EventFilters.Count.ShouldBe(2);
    }

    [Fact]
    public async Task HIS_U_018_A_raw_series_over_the_limit_is_refused_and_asks_for_a_bucket()
    {
        var device = Watchpoint.Testing.Ids.TestIds.Device(1);
        var path = PropertyPath.FromTrusted("temperature.value");
        HistorySeriesRow Raw(int i) => new(_from.AddSeconds(i), i, null, null, null, null, null, null, 1);
        _store.SeriesQuery = request => [.. Enumerable.Range(0, Math.Min(request.Limit, 20_000)).Select(Raw)];

        var error = (await Queries().QuerySeriesAsync(new SeriesQuery(device, path, _from, _from.AddDays(1), null), Ct)).ShouldFailWith("history.query.too_many_points");
        error.Kind.ShouldBe(ErrorKind.Unprocessable);
        error.Args["max"].ShouldBe(10_000);
        _store.SeriesRequests.Single().Limit.ShouldBe(10_001);

        // Exactly 10,000 raw points are fine; they come back as values.
        _store.SeriesQuery = request => [.. Enumerable.Range(0, 10_000).Select(Raw)];
        var points = (await Queries().QuerySeriesAsync(new SeriesQuery(device, path, _from, _from.AddDays(1), null), Ct)).ShouldBeSuccess();
        points.Count.ShouldBe(10_000);
        points[5].Value.ShouldNotBeNull().AsDecimal().ShouldBe(5m);
        points[5].Count.ShouldBe(1);

        // A bucketed series returns min, average and max, and too many buckets is the same error.
        _store.SeriesQuery = request => [new HistorySeriesRow(_from, null, null, null, null, 1m, 2m, 3m, 4)];
        var bucketed = (await Queries().QuerySeriesAsync(new SeriesQuery(device, path, _from, _from.AddDays(1), TimeSpan.FromHours(1)), Ct)).ShouldBeSuccess();
        bucketed.Single().ShouldBe(new SeriesPoint(_from, null, 1m, 2m, 3m, 4));
        _store.SeriesQuery = request => [.. Enumerable.Range(0, 10_001).Select(Raw)];
        (await Queries().QuerySeriesAsync(new SeriesQuery(device, path, _from, _from.AddDays(1), TimeSpan.FromSeconds(1)), Ct)).ShouldFailWith("history.query.too_many_points");

        // Reversed ranges and absurd buckets are refused before the database is asked.
        var calls = _store.SeriesRequests.Count;
        (await Queries().QuerySeriesAsync(new SeriesQuery(device, path, _from.AddDays(1), _from, null), Ct)).ShouldFailWith("history.query.invalid");
        (await Queries().QuerySeriesAsync(new SeriesQuery(device, path, _from, _from.AddDays(1), TimeSpan.FromMilliseconds(10)), Ct)).ShouldFailWith("history.query.invalid");
        _store.SeriesRequests.Count.ShouldBe(calls);
    }

    [Fact]
    public async Task HIS_U_019_Count_queries_accept_only_whitelisted_tables_columns_and_filters()
    {
        var q = (string table, string[] groupBy, string filters) => new CountQuery(table, _from, _from.AddDays(7), TimeSpan.FromDays(1), groupBy, JsonDocument.Parse(filters).RootElement.Clone());

        // Unknown table, group-by column and filter column are refused with the field named; the database is not asked.
        (await Queries().CountAsync(q("users", ["kind"], "{}"), Ct)).ShouldFailWith("history.query.invalid").Args["field"].ShouldBe("table");
        (await Queries().CountAsync(q("events", ["payload; DROP TABLE x"], "{}"), Ct)).ShouldFailWith("history.query.invalid").Args["field"].ShouldBe("groupBy");
        (await Queries().CountAsync(q("events", ["personal"], "{}"), Ct)).ShouldFailWith("history.query.invalid");
        (await Queries().CountAsync(q("events", ["rule_key"], "{}"), Ct)).ShouldFailWith("history.query.invalid");
        (await Queries().CountAsync(q("events", ["kind"], """{"person_refs":"x"}"""), Ct)).ShouldFailWith("history.query.invalid").Args["field"].ShouldBe("filters.person_refs");
        (await Queries().CountAsync(q("events", ["kind"], """{"severity":"high"}"""), Ct)).ShouldFailWith("history.query.invalid").Args["reason"].ShouldBe("value");
        (await Queries().CountAsync(q("events", ["kind"], """{"entity_id":"not-a-guid"}"""), Ct)).ShouldFailWith("history.query.invalid");
        (await Queries().CountAsync(q("events", ["kind"], """{"kind":[]}"""), Ct)).ShouldFailWith("history.query.invalid");
        (await Queries().CountAsync(q("events", ["kind"], "[1]"), Ct)).ShouldFailWith("history.query.invalid");
        _store.CountRequests.ShouldBeEmpty();

        // Whitelisted ones pass, values are normalised, scope and bucket reach the store.
        var entity = Watchpoint.Testing.Ids.TestIds.Uuid(7);
        var ok = new CountQuery(
            "events", _from, _from.AddDays(7), TimeSpan.FromDays(1), ["kind", "severity", "location_id"],
            JsonDocument.Parse($$"""{"severity":[5,6],"kind":"door.forced","entity_id":"{{entity.ToString("D").ToUpperInvariant()}}"}""").RootElement.Clone(),
            new HistoryScope(null, [Watchpoint.Testing.Ids.TestIds.Device(1)]));
        (await Queries().CountAsync(ok, Ct)).ShouldBeSuccess();
        var request = _store.CountRequests.ShouldHaveSingleItem();
        request.Table.ShouldBe("events");
        request.GroupBy.ShouldBe(["kind", "severity", "location_id"]);
        request.Filters.Select(f => (f.Column, string.Join("|", f.Values))).ShouldBe([("severity", "5|6"), ("kind", "door.forced"), ("entity_id", entity.ToString("D"))]);
        request.Scope.EntityIds.ShouldBe([Watchpoint.Testing.Ids.TestIds.Device(1).Value]);
        request.Bucket.ShouldBe(TimeSpan.FromDays(1));
        foreach (var table in HistoryCountColumns.Tables)
        {
            HistoryCountColumns.TryGetColumns(table, out var columns).ShouldBeTrue();
            columns.ShouldNotBeEmpty();
        }

        // Range limits: 366 days for counts, 31 days for events.
        (await Queries().CountAsync(new CountQuery("events", _from, _from.AddDays(367), TimeSpan.FromDays(1), [], default), Ct)).ShouldFailWith("history.query.range_too_large").Args["maxDays"].ShouldBe(366);
        (await Queries().CountAsync(new CountQuery("events", _from, _from.AddDays(366), TimeSpan.FromDays(1), [], default), Ct)).ShouldBeSuccess();
        (await Queries().QueryEventsAsync(new EventQuery(_from, _from.AddDays(32)), new PageRequest(10), Ct)).ShouldFailWith("history.query.range_too_large").Args["maxDays"].ShouldBe(31);
        (await Queries().QueryEventsAsync(new EventQuery(_from, _from.AddDays(31)), new PageRequest(10), Ct)).ShouldBeSuccess();
        (await Queries().CountAsync(new CountQuery("events", _from, _from, TimeSpan.FromDays(1), [], default), Ct)).ShouldFailWith("history.query.invalid");
        (await Queries().CountAsync(new CountQuery("events", _from, _from.AddDays(1), TimeSpan.Zero, [], default), Ct)).ShouldFailWith("history.query.invalid");
    }

    private static HistoryEventRow Row(DateTimeOffset time, Guid id) => new(
        time, id, 1, "door.forced", 0, 5, null, null, null, null, null, null, null, null, 0, null, "{}", null, null, "k", "{}", null, null, null);

    /// <summary>The keyset seek of the SQL, on a list: ordered by (time, id), strictly after the cursor position.</summary>
    private static IReadOnlyList<HistoryEventRow> Seek(List<HistoryEventRow> all, HistoryEventFilter f, int limit)
    {
        var ordered = f.Descending ? all.OrderByDescending(r => r.Time).ThenByDescending(r => r.Id) : all.OrderBy(r => r.Time).ThenBy(r => r.Id);
        return [.. ordered
            .Where(r => r.Time >= f.From && r.Time < f.To)
            .Where(r => f.After is null || (f.Descending
                ? (r.Time, r.Id).CompareTo((f.After.Time, f.After.Id)) < 0
                : (r.Time, r.Id).CompareTo((f.After.Time, f.After.Id)) > 0))
            .Take(limit)];
    }

    [Fact]
    public async Task HIS_U_020_Keyset_paging_is_stable_when_rows_arrive_between_pages()
    {
        // 25 rows, several sharing a time: the id breaks ties.
        var rows = Enumerable.Range(0, 25).Select(i => Row(_from.AddMinutes(i / 3), Watchpoint.Testing.Ids.TestIds.Uuid(1000 + i))).ToList();
        _store.EventQuery = (f, limit) => Seek(rows, f, limit);
        var queries = Queries();

        var seen = new List<Guid>();
        string? cursor = null;
        var pages = 0;
        do
        {
            var page = (await queries.QueryEventsAsync(Events(), new PageRequest(10, cursor), Ct)).ShouldBeSuccess();
            pages++;
            page.Items.Count.ShouldBeLessThanOrEqualTo(10);
            seen.AddRange(page.Items.Select(e => e.Id));

            // Rows that arrive after the first page, newer and older, never shift the pages that follow.
            if (pages == 1)
            {
                rows.Add(Row(_from.AddHours(20), Watchpoint.Testing.Ids.TestIds.Uuid(5000)));
                rows.Add(Row(_from.AddMinutes(-1), Watchpoint.Testing.Ids.TestIds.Uuid(5001)));
            }

            cursor = page.NextCursor;
        }
        while (cursor is not null);

        pages.ShouldBe(3);
        seen.Count.ShouldBe(25);
        seen.Distinct().Count().ShouldBe(25);
        seen.ShouldNotContain(Watchpoint.Testing.Ids.TestIds.Uuid(5000));
        var expected = rows.Where(r => r.Time >= _from && r.Id != Watchpoint.Testing.Ids.TestIds.Uuid(5000)).OrderByDescending(r => r.Time).ThenByDescending(r => r.Id).Select(r => r.Id);
        seen.ShouldBe(expected);

        // The store was asked for one row more than the page, to know whether another page follows; the last page has no cursor.
        _store.EventFilters.ShouldAllBe(f => f.Descending);
        _store.EventFilters.Select(f => f.After is null).ShouldBe([true, false, false]);

        // Ascending order pages the same way, from the other end.
        var ascending = (await queries.QueryEventsAsync(Events(descending: false), new PageRequest(5), Ct)).ShouldBeSuccess();
        ascending.Items.Select(e => e.Id).ShouldBe(rows.Where(r => r.Time >= _from && r.Id != Watchpoint.Testing.Ids.TestIds.Uuid(5000)).OrderBy(r => r.Time).ThenBy(r => r.Id).Take(5).Select(r => r.Id));
        ascending.NextCursor.ShouldNotBeNull();

        // An exact fit has no next cursor; a limit above the maximum and a cursor that is not ours are refused.
        var exact = (await queries.QueryEventsAsync(Events(), new PageRequest(26), Ct)).ShouldBeSuccess();
        exact.NextCursor.ShouldBeNull();
        (await queries.QueryEventsAsync(Events(), new PageRequest(1001), Ct)).ShouldFailWith("common.value.out_of_range");
        (await queries.QueryEventsAsync(Events(), new PageRequest(10, "garbage"), Ct)).ShouldFailWith("common.cursor.invalid");
        var foreign = CursorCodec.Encode(2, [_from], Guid.NewGuid());
        (await queries.QueryEventsAsync(Events(), new PageRequest(10, foreign), Ct)).ShouldFailWith("common.cursor.invalid");
        var wrongKey = CursorCodec.Encode(1, [5], Guid.NewGuid());
        (await queries.QueryEventsAsync(Events(), new PageRequest(10, wrongKey), Ct)).ShouldFailWith("common.cursor.invalid");

        // A statement timeout of the database is an error value, not an exception.
        _store.EventQuery = (_, _) => throw new HistoryQueryTimeoutException();
        (await queries.QueryEventsAsync(Events(), new PageRequest(10), Ct)).ShouldFailWith("history.query.timeout").Kind.ShouldBe(ErrorKind.Unavailable);
    }
}
