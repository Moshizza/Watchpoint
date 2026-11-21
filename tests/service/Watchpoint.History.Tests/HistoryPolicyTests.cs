using Shouldly;
using Watchpoint.History.Contracts;
using Watchpoint.History.Internal.Policies;
using Watchpoint.Kernel.Values;
using Watchpoint.Model.Contracts;
using Watchpoint.Storage.Contracts;
using Xunit;

namespace Watchpoint.History.Tests;

[Trait("layer", "U")]
[Trait("spec", "HIS")]
public sealed class HistoryPolicyTests
{
    private const string Temp = "temperature.value";

    private static readonly decimal[] _deadbandSeries = [20m, 20.5m, 21m, 22.5m, 22.9m, 25m];

    private static readonly string[] _everyChangeNames = ["everyChange", "all"];

    private static CancellationToken Ct => TestContext.Current.CancellationToken;

    private static HistoryPropertyRow Row(DateTimeOffset time, decimal? value, short quality = 0, string? text = null) =>
        new(time, HistoryHarness.Door.Value, Temp, value, text, null, null, quality, null, Guid.NewGuid());

    [Fact]
    public async Task HIS_U_008_EveryChange_policy_stores_every_record()
    {
        // "all" is the catalogue's name for everyChange, and a property without a definition is recorded too.
        foreach (var mode in _everyChangeNames)
        {
            using var h = new HistoryHarness(null, (Temp, new HistoryPolicy(mode)));
            var records = Enumerable.Range(0, 5).Select(i => h.Property(i + 1, Temp, WpValue.Decimal(20))).ToList();
            records.Add(h.Property(6, "unknown.thing", WpValue.Int(1)));

            var written = await h.PropertyWriter().WriteAsync(records, Ct);

            written.ShouldBe(6);
            h.Store.PropertyWrites.ShouldHaveSingleItem().Count.ShouldBe(6);
            h.Store.LastRowLookups.ShouldBeEmpty();
        }

        PropertyHistoryPolicy.EveryChange.ShouldBe(PropertyHistoryPolicy.From("everyChange", null, null, null));
        PropertyHistoryPolicy.From("unheardOf", null, null, null).Mode.ShouldBe(HistoryMode.EveryChange);
    }

    [Fact]
    public async Task HIS_U_009_Deadband_absolute_percent_quality_change_and_max_interval()
    {
        var t0 = new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero);
        var absolute = new PropertyHistoryPolicy(HistoryMode.Deadband, Percent: false, Threshold: 2m);
        var percent = new PropertyHistoryPolicy(HistoryMode.Deadband, Percent: true, Threshold: 10m);
        var last = Row(t0, 100m);

        // Absolute: |v - last| >= 2.
        absolute.ShouldStore(Row(t0.AddSeconds(1), 101.9m), last).ShouldBeFalse();
        absolute.ShouldStore(Row(t0.AddSeconds(1), 102m), last).ShouldBeTrue();
        absolute.ShouldStore(Row(t0.AddSeconds(1), 98m), last).ShouldBeTrue();
        absolute.ShouldStore(Row(t0.AddSeconds(1), 98.1m), last).ShouldBeFalse();

        // Percent: 10 % of the last stored value (100 → 10).
        percent.ShouldStore(Row(t0.AddSeconds(1), 109.9m), last).ShouldBeFalse();
        percent.ShouldStore(Row(t0.AddSeconds(1), 110m), last).ShouldBeTrue();
        percent.ShouldStore(Row(t0.AddSeconds(1), 90m), last).ShouldBeTrue();

        // First value, quality change and max interval always store; an unchanged value never does.
        absolute.ShouldStore(Row(t0, 100m), null).ShouldBeTrue();
        absolute.ShouldStore(Row(t0.AddSeconds(1), 100m, quality: 2), last).ShouldBeTrue();
        absolute.ShouldStore(Row(t0.AddMinutes(59), 100m), last).ShouldBeFalse();
        absolute.ShouldStore(Row(t0.AddHours(1), 100m), last).ShouldBeTrue();
        new PropertyHistoryPolicy(HistoryMode.Deadband).ShouldStore(Row(t0.AddSeconds(1), 100m), last).ShouldBeFalse();

        // Non-numeric values: stored when they change.
        var text = Row(t0, null, text: "closed");
        absolute.ShouldStore(Row(t0.AddSeconds(1), null, text: "closed"), text).ShouldBeFalse();
        absolute.ShouldStore(Row(t0.AddSeconds(1), null, text: "open"), text).ShouldBeTrue();

        // Through the writer: each record is compared with the last one stored, in the same batch too.
        using var h = new HistoryHarness(null, (Temp, new HistoryPolicy("deadband", "absolute", 2)));
        var records = _deadbandSeries.Select((v, i) => h.Property(i + 1, Temp, WpValue.Decimal(v))).ToList();
        (await h.PropertyWriter().WriteAsync(records, Ct)).ShouldBe(3);
        h.Store.PropertyWrites.Single().Select(r => r.VNum).ShouldBe([20m, 22.5m, 25m]);
        PropertyHistoryPolicy.From("deadband", "percent", 5, null).ShouldBe(new PropertyHistoryPolicy(HistoryMode.Deadband, true, 5m));
    }

    [Fact]
    public async Task HIS_U_010_Periodic_sampler_aligns_to_boundaries_and_stores_once_per_boundary()
    {
        var now = new DateTimeOffset(2026, 1, 5, 9, 7, 31, 250, TimeSpan.Zero);
        PeriodicSampler.NextBoundary(now, TimeSpan.FromMinutes(1)).ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 8, 0, TimeSpan.Zero));
        PeriodicSampler.NextBoundary(now, TimeSpan.FromMinutes(15)).ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 15, 0, TimeSpan.Zero));
        PeriodicSampler.NextBoundary(now, TimeSpan.FromHours(1)).ShouldBe(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero));
        PeriodicSampler.NextBoundary(now, TimeSpan.FromDays(1)).ShouldBe(new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero));
        PeriodicSampler.NextBoundary(now, TimeSpan.FromSeconds(10)).ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 7, 40, TimeSpan.Zero));

        // At a boundary the next one is a full interval away: a sample is never taken twice for one boundary.
        var boundary = new DateTimeOffset(2026, 1, 5, 9, 15, 0, TimeSpan.Zero);
        PeriodicSampler.NextBoundary(boundary, TimeSpan.FromMinutes(15)).ShouldBe(boundary.AddMinutes(15));
        PeriodicSampler.BoundaryAtOrBefore(boundary, TimeSpan.FromMinutes(15)).ShouldBe(boundary);
        PeriodicSampler.BoundaryAtOrBefore(now, TimeSpan.FromMinutes(15)).ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));

        using var h = new HistoryHarness();
        var source = new FakePeriodicSource(TimeSpan.FromMinutes(1))
        {
            Values = [new PeriodicPropertyValue(HistoryHarness.Door, Temp, WpValue.Decimal(21.5m), Quality.Good, null)],
        };
        var sampler = new PeriodicSampler(source, h.Store, h.Time, Microsoft.Extensions.Logging.Abstractions.NullLogger<PeriodicSampler>.Instance);
        var sampleBoundary = new DateTimeOffset(2026, 1, 5, 9, 8, 0, TimeSpan.Zero);

        (await sampler.SampleAsync(TimeSpan.FromMinutes(1), sampleBoundary, Ct)).ShouldBe(1);
        await sampler.SampleAsync(TimeSpan.FromMinutes(1), sampleBoundary, Ct);
        var first = h.Store.PropertyWrites[0].Single();
        first.Time.ShouldBe(sampleBoundary);
        first.VNum.ShouldBe(21.5m);

        // The id depends only on device, path and boundary, so storing a boundary twice is one row for the database.
        h.Store.PropertyWrites[1].Single().RecordId.ShouldBe(first.RecordId);
        PeriodicSampler.SampleId(HistoryHarness.Door.Value, Temp, sampleBoundary.AddMinutes(1)).ShouldNotBe(first.RecordId);

        // The loop wakes at the next boundary and stores with that boundary as time.
        h.Store.PropertyWrites.Clear();
        h.Time.SetUtcNow(new DateTimeOffset(2026, 1, 5, 9, 8, 20, TimeSpan.Zero));
        var sampled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        h.Store.OnAttempt = () => sampled.TrySetResult();
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(Ct);
        var loop = sampler.RunAsync(cts.Token);
        for (var i = 0; i < 100_000 && !sampled.Task.IsCompleted; i++)
        {
            h.Time.Advance(TimeSpan.FromSeconds(1));
            await Task.Yield();
        }

        sampled.Task.IsCompleted.ShouldBeTrue();
        await cts.CancelAsync();
        await loop;
        h.Store.PropertyWrites.First().Single().Time.Ticks.ShouldBe(PeriodicSampler.BoundaryAtOrBefore(h.Store.PropertyWrites.First().Single().Time, TimeSpan.FromMinutes(1)).Ticks);
    }

    [Fact]
    public async Task HIS_U_011_None_and_periodic_policies_store_nothing_from_records()
    {
        using var h = new HistoryHarness(null, (Temp, new HistoryPolicy("none")), ("pressure.value", new HistoryPolicy("periodic", Interval: TimeSpan.FromMinutes(1))));
        var records = new[]
        {
            h.Property(1, Temp, WpValue.Decimal(1)),
            h.Property(2, "pressure.value", WpValue.Decimal(1)),
            h.Property(3, Temp, WpValue.Decimal(2)),
        };

        (await h.PropertyWriter().WriteAsync(records, Ct)).ShouldBe(0);

        h.Store.PropertyWrites.ShouldHaveSingleItem().ShouldBeEmpty();
        PropertyHistoryPolicy.From("none", null, null, null).ShouldBe(new PropertyHistoryPolicy(HistoryMode.None));
        PropertyHistoryPolicy.From("periodic", null, null, TimeSpan.FromSeconds(30)).Interval.ShouldBe(TimeSpan.FromSeconds(30));
    }

    [Fact]
    public async Task HIS_U_012_Policy_override_precedence_device_then_type_then_definition()
    {
        using var h = new HistoryHarness(null, (Temp, new HistoryPolicy("everyChange")));
        h.Store.Overrides.Add(new HistoryPolicyOverrideRow("type", "ac.door", Temp, """{"mode":"deadband","type":"absolute","threshold":5}"""));
        h.Store.Overrides.Add(new HistoryPolicyOverrideRow("device", HistoryHarness.Door2.ToString(), Temp, """{"mode":"none"}"""));
        h.Store.Overrides.Add(new HistoryPolicyOverrideRow("device", HistoryHarness.Door2.ToString(), "other.path", "not json"));
        await h.Policies.RefreshAsync(Ct);

        // Device override (none) beats the type override (deadband), which beats the definition (everyChange).
        h.Policies.Resolve(HistoryHarness.Door2, HistoryHarness.DoorType, Temp).Mode.ShouldBe(HistoryMode.None);
        var byType = h.Policies.Resolve(HistoryHarness.Door, HistoryHarness.DoorType, Temp);
        byType.Mode.ShouldBe(HistoryMode.Deadband);
        byType.Threshold.ShouldBe(5m);
        h.Policies.Resolve(HistoryHarness.Door, HistoryHarness.DoorType, "other.path").Mode.ShouldBe(HistoryMode.EveryChange);

        // Overrides are cached, and reloaded after the refresh period.
        h.Store.Overrides.Clear();
        await h.Policies.RefreshAsync(Ct);
        h.Policies.Resolve(HistoryHarness.Door2, HistoryHarness.DoorType, Temp).Mode.ShouldBe(HistoryMode.None);
        h.Time.Advance(h.Settings.PolicyOverrideRefresh);
        await h.Policies.RefreshAsync(Ct);
        h.Policies.Resolve(HistoryHarness.Door2, HistoryHarness.DoorType, Temp).Mode.ShouldBe(HistoryMode.EveryChange);
    }

    [Fact]
    public async Task HIS_U_013_Last_stored_cache_is_rebuilt_lazily_and_only_after_a_committed_write()
    {
        using var h = new HistoryHarness(null, (Temp, new HistoryPolicy("deadband", "absolute", 2)));
        var t0 = h.Time.GetUtcNow();
        h.Store.LastRows.Add(Row(t0.AddMinutes(-1), 50m));
        var writer = h.PropertyWriter();

        // First sight of the key: the latest stored row is read once and compared with.
        (await writer.WriteAsync([h.Property(1, Temp, WpValue.Decimal(50.5m))], Ct)).ShouldBe(0);
        h.Store.LastRowLookups.ShouldHaveSingleItem().ShouldBe([(HistoryHarness.Door.Value, Temp)]);
        h.Cache.Count.ShouldBe(1);

        // The cache answers from then on, and follows what is stored.
        (await writer.WriteAsync([h.Property(2, Temp, WpValue.Decimal(53m)), h.Property(3, Temp, WpValue.Decimal(54m))], Ct)).ShouldBe(1);
        h.Store.LastRowLookups.Count.ShouldBe(1);
        h.Cache.TryGet(HistoryHarness.Door.Value, Temp, out var cached).ShouldBeTrue();
        cached.VNum.ShouldBe(53m);

        // A failed write must not advance the cache: the retry decides the same.
        h.Store.FailNext(1);
        await Should.ThrowAsync<InvalidOperationException>(() => writer.WriteAsync([h.Property(4, Temp, WpValue.Decimal(60m))], Ct));
        h.Cache.TryGet(HistoryHarness.Door.Value, Temp, out cached).ShouldBeTrue();
        cached.VNum.ShouldBe(53m);
        (await writer.WriteAsync([h.Property(4, Temp, WpValue.Decimal(60m))], Ct)).ShouldBe(1);

        // A full cache is emptied and rebuilt from the database on next use.
        var small = new LastStoredCache(1);
        small.Set(Row(t0, 1m));
        small.Set(new HistoryPropertyRow(t0, HistoryHarness.Door2.Value, Temp, 2m, null, null, null, 0, null, Guid.NewGuid()));
        small.Count.ShouldBe(1);
        small.TryGet(HistoryHarness.Door.Value, Temp, out _).ShouldBeFalse();
    }

    private sealed class FakePeriodicSource(TimeSpan interval) : IPeriodicPropertySource
    {
        public IReadOnlyCollection<TimeSpan> Intervals { get; } = [interval];

        public List<PeriodicPropertyValue> Values { get; set; } = [];

        public async IAsyncEnumerable<PeriodicPropertyValue> GetValuesAsync(TimeSpan interval, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var value in Values)
            {
                yield return value;
            }

            await Task.CompletedTask;
        }
    }
}
