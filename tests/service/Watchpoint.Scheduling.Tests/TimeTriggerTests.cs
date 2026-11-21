using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Testing;
using Watchpoint.Kernel.Text;
using Watchpoint.Kernel.Values;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Scheduling.Tests;

[Trait("layer", "U")]
[Trait("spec", "SCH")]
public sealed class TimeTriggerTests
{
    [Fact]
    public void SCH_U_016_At_trigger_fires_once()
    {
        var time = TestTime.CreateProvider();
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        int fireCount = 0;
        var targetTime = time.GetUtcNow().AddMinutes(10);

        using var reg = triggerService.Register("test", "one-shot", new TriggerSpec.At(targetTime), MissedPolicy.Skip, (fired, ct) =>
        {
            fireCount++;
            return ValueTask.CompletedTask;
        });

        triggerService.NextFire("test", "one-shot").ShouldBe(targetTime);

        // Before fire time
        triggerService.ProcessDue(time.GetUtcNow().AddMinutes(5));
        fireCount.ShouldBe(0);

        // At/past fire time
        triggerService.ProcessDue(time.GetUtcNow().AddMinutes(10));
        fireCount.ShouldBe(1);

        // Subsequent check: should not fire again
        triggerService.ProcessDue(time.GetUtcNow().AddMinutes(30));
        fireCount.ShouldBe(1);
    }

    [Fact]
    public void SCH_U_017_Cron_trigger_with_time_zone_incl_DST_days()
    {
        var start = new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero);
        var time = TestTime.CreateProvider(start);
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        int firedCount = 0;
        using var reg = triggerService.Register("test", "cron-dst", new TriggerSpec.Cron("0 5 * * *", "Europe/Paris"), MissedPolicy.Skip, (fired, ct) =>
        {
            firedCount++;
            return ValueTask.CompletedTask;
        });

        var nextFire = triggerService.NextFire("test", "cron-dst");
        nextFire.ShouldBe(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.Zero));

        triggerService.ProcessDue(new DateTimeOffset(2026, 3, 29, 3, 0, 0, TimeSpan.Zero));
        firedCount.ShouldBe(1);
    }

    [Fact]
    public void SCH_U_018_Invalid_cron_rejected()
    {
        var time = TestTime.CreateProvider();
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        Should.Throw<ArgumentException>(() =>
            triggerService.Register("test", "invalid-6-fields", new TriggerSpec.Cron("0 0 12 * * *", "UTC"), MissedPolicy.Skip, (_, _) => ValueTask.CompletedTask));

        Should.Throw<ArgumentException>(() =>
            triggerService.Register("test", "invalid-syntax", new TriggerSpec.Cron("not-a-cron", "UTC"), MissedPolicy.Skip, (_, _) => ValueTask.CompletedTask));
    }

    [Fact]
    public void SCH_U_019_Calendar_edge_triggers_at_starts_ends()
    {
        var time = TestTime.CreateProvider(new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero));
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);

        var cal = new Calendar(
            CalendarId.New(),
            "office",
            LocalizedName.Of("Office"),
            "UTC",
            new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
            {
                [IsoDayOfWeek.Monday] = [new TimeRange(new LocalTime(9, 0), new LocalTime(17, 0))],
            },
            [],
            [],
            null,
            null,
            1);
        calService.Upsert(cal).IsSuccess.ShouldBeTrue();

        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        int startsFired = 0;
        int endsFired = 0;

        using var regStart = triggerService.Register("test", "cal-start", new TriggerSpec.CalendarEdge("office", EdgeKind.Start), MissedPolicy.Skip, (_, _) =>
        {
            startsFired++;
            return ValueTask.CompletedTask;
        });

        using var regEnd = triggerService.Register("test", "cal-end", new TriggerSpec.CalendarEdge("office", EdgeKind.End), MissedPolicy.Skip, (_, _) =>
        {
            endsFired++;
            return ValueTask.CompletedTask;
        });

        triggerService.NextFire("test", "cal-start").ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));
        triggerService.NextFire("test", "cal-end").ShouldBe(new DateTimeOffset(2026, 1, 5, 17, 0, 0, TimeSpan.Zero));

        triggerService.ProcessDue(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));
        startsFired.ShouldBe(1);
        endsFired.ShouldBe(0);

        triggerService.ProcessDue(new DateTimeOffset(2026, 1, 5, 17, 0, 0, TimeSpan.Zero));
        startsFired.ShouldBe(1);
        endsFired.ShouldBe(1);
    }

    [Fact]
    public void SCH_U_020_Calendar_change_recomputes_edge_triggers()
    {
        var time = TestTime.CreateProvider(new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero));
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);

        var calId = CalendarId.New();
        var cal = new Calendar(
            calId,
            "office",
            LocalizedName.Of("Office"),
            "UTC",
            new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
            {
                [IsoDayOfWeek.Monday] = [new TimeRange(new LocalTime(9, 0), new LocalTime(17, 0))],
            },
            [],
            [],
            null,
            null,
            1);
        calService.Upsert(cal).IsSuccess.ShouldBeTrue();

        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        using var reg = triggerService.Register("test", "cal-start", new TriggerSpec.CalendarEdge("office", EdgeKind.Start), MissedPolicy.Skip, (_, _) => ValueTask.CompletedTask);

        triggerService.NextFire("test", "cal-start").ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));

        var updatedCal = new Calendar(
            calId,
            "office",
            LocalizedName.Of("Office Updated"),
            "UTC",
            new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
            {
                [IsoDayOfWeek.Monday] = [new TimeRange(new LocalTime(10, 0), new LocalTime(17, 0))],
            },
            [],
            [],
            null,
            null,
            2);
        calService.Upsert(updatedCal).IsSuccess.ShouldBeTrue();

        triggerService.NextFire("test", "cal-start").ShouldBe(new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void SCH_U_021_Every_interval_with_anchor()
    {
        var anchor = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var start = anchor.AddSeconds(35);
        var time = TestTime.CreateProvider(start);

        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        int firedCount = 0;
        using var reg = triggerService.Register("test", "every-30s", new TriggerSpec.Every(TimeSpan.FromSeconds(30), anchor), MissedPolicy.Skip, (_, _) =>
        {
            firedCount++;
            return ValueTask.CompletedTask;
        });

        triggerService.NextFire("test", "every-30s").ShouldBe(anchor.AddSeconds(60));

        triggerService.ProcessDue(anchor.AddSeconds(60));
        firedCount.ShouldBe(1);

        triggerService.NextFire("test", "every-30s").ShouldBe(anchor.AddSeconds(90));
    }

    [Fact]
    public void SCH_U_022_Sunrise_sunset_for_Paris_Casablanca_Dubai_within_2_min_of_reference()
    {
        var date = new DateOnly(2026, 6, 21);

        var (parisRise, parisSet) = SolarCalculator.Calculate(48.8566, 2.3522, date);
        parisRise.ShouldNotBeNull();
        parisSet.ShouldNotBeNull();
        Math.Abs((parisRise.Value - new DateTimeOffset(2026, 6, 21, 3, 47, 0, TimeSpan.Zero)).TotalMinutes).ShouldBeLessThanOrEqualTo(2.0);
        Math.Abs((parisSet.Value - new DateTimeOffset(2026, 6, 21, 19, 58, 0, TimeSpan.Zero)).TotalMinutes).ShouldBeLessThanOrEqualTo(2.0);

        var (casaRise, casaSet) = SolarCalculator.Calculate(33.5731, -7.5898, date);
        casaRise.ShouldNotBeNull();
        casaSet.ShouldNotBeNull();
        Math.Abs((casaRise.Value - new DateTimeOffset(2026, 6, 21, 5, 21, 0, TimeSpan.Zero)).TotalMinutes).ShouldBeLessThanOrEqualTo(2.0);
        Math.Abs((casaSet.Value - new DateTimeOffset(2026, 6, 21, 19, 44, 0, TimeSpan.Zero)).TotalMinutes).ShouldBeLessThanOrEqualTo(2.0);

        var (dubaiRise, dubaiSet) = SolarCalculator.Calculate(25.2048, 55.2708, date);
        dubaiRise.ShouldNotBeNull();
        dubaiSet.ShouldNotBeNull();
        Math.Abs((dubaiRise.Value - new DateTimeOffset(2026, 6, 21, 1, 29, 0, TimeSpan.Zero)).TotalMinutes).ShouldBeLessThanOrEqualTo(2.0);
        Math.Abs((dubaiSet.Value - new DateTimeOffset(2026, 6, 21, 15, 12, 0, TimeSpan.Zero)).TotalMinutes).ShouldBeLessThanOrEqualTo(2.0);
    }

    [Fact]
    public void SCH_U_023_Polar_day_no_event_logged()
    {
        using var logs = new LogCollector();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var logger = loggerFactory.CreateLogger<TimeTriggerService>();

        var time = TestTime.CreateProvider(new DateTimeOffset(2026, 6, 21, 0, 0, 0, TimeSpan.Zero));
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider(new GeoPoint(69.6492, 18.9553, null));

        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, logger);

        using var reg = triggerService.Register("test", "sun-polar", new TriggerSpec.Sun(SunEvent.Sunrise, TimeSpan.Zero, null), MissedPolicy.Skip, (_, _) => ValueTask.CompletedTask);

        logs.WithEventId(2353).ShouldNotBeEmpty();
    }

    [Fact]
    public async Task SCH_U_024_Missed_FireOnce_fires_once_with_CatchUp_after_restart()
    {
        var start = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
        var time = TestTime.CreateProvider(start);
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();

        var ct = TestContext.Current.CancellationToken;

        await stateStore.SetLastFiredAsync("owner1", "hourly", start.AddHours(-2), ct);

        bool caughtUp = false;
        var tcs = new TaskCompletionSource<bool>();

        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        using var reg = triggerService.Register("owner1", "hourly", new TriggerSpec.Cron("30 * * * *", "UTC"), MissedPolicy.FireOnce, (fired, token) =>
        {
            if (fired.CatchUp)
            {
                caughtUp = true;
                tcs.TrySetResult(true);
            }

            return ValueTask.CompletedTask;
        });

        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(5), ct);
        caughtUp.ShouldBeTrue();
    }

    [Fact]
    public async Task SCH_U_025_Missed_Skip_does_not_fire()
    {
        var start = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
        var time = TestTime.CreateProvider(start);
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();

        var ct = TestContext.Current.CancellationToken;

        await stateStore.SetLastFiredAsync("owner1", "hourly-skip", start.AddHours(-2), ct);

        int firedCount = 0;
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        using var reg = triggerService.Register("owner1", "hourly-skip", new TriggerSpec.Cron("30 * * * *", "UTC"), MissedPolicy.Skip, (_, _) =>
        {
            firedCount++;
            return ValueTask.CompletedTask;
        });

        await Task.Yield();
        firedCount.ShouldBe(0);

        triggerService.NextFire("owner1", "hourly-skip").ShouldBe(start.AddMinutes(30));
    }

    [Fact]
    public async Task SCH_U_026_Last_fired_persisted_only_after_callback_completion()
    {
        var start = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
        var time = TestTime.CreateProvider(start);
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var callbackFinished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stateStore = new SignallingTriggerStateStore(callbackFinished.Task);
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        var ct = TestContext.Current.CancellationToken;

        var inCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseCallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

        using var reg = triggerService.Register("test", "persist-test", new TriggerSpec.At(start.AddSeconds(10)), MissedPolicy.Skip, async (fired, token) =>
        {
            inCallback.TrySetResult();
            await releaseCallback.Task.WaitAsync(token);
            callbackFinished.TrySetResult();
        });

        // Fire via ProcessDue in background task so we can assert mid-execution
        var fireTask = Task.Run(() => triggerService.ProcessDue(start.AddSeconds(10)), ct);

        await inCallback.Task.WaitAsync(ct);

        (await stateStore.GetLastFiredAsync("test", "persist-test", ct)).ShouldBeNull();
        stateStore.Persisted.IsCompleted.ShouldBeFalse();

        releaseCallback.TrySetResult();

        // The write happens after the callback returns, on the dispatcher's continuation: wait for the write itself
        // (a signal), not for the callback's last statement, which runs before it.
        var persistedAt = await stateStore.Persisted.WaitAsync(ct);
        await fireTask;

        stateStore.CallbackCompletedBeforePersist.ShouldBeTrue();
        persistedAt.ShouldBe(start.AddSeconds(10));
        (await stateStore.GetLastFiredAsync("test", "persist-test", ct)).ShouldBe(start.AddSeconds(10));
    }

    /// <summary>
    /// In-memory state store that signals the first <see cref="SetLastFiredAsync"/> and records whether the callback had
    /// completed at that moment.
    /// </summary>
    private sealed class SignallingTriggerStateStore(Task callbackFinished) : ITriggerStateStore
    {
        private readonly InMemoryTriggerStateStore _inner = new();
        private readonly TaskCompletionSource<DateTimeOffset> _persisted = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task<DateTimeOffset> Persisted => _persisted.Task;

        public bool CallbackCompletedBeforePersist { get; private set; }

        public ValueTask<DateTimeOffset?> GetLastFiredAsync(string owner, string key, CancellationToken ct = default) =>
            _inner.GetLastFiredAsync(owner, key, ct);

        public async ValueTask SetLastFiredAsync(string owner, string key, DateTimeOffset firedAt, CancellationToken ct = default)
        {
            CallbackCompletedBeforePersist = callbackFinished.IsCompleted;
            await _inner.SetLastFiredAsync(owner, key, firedAt, ct).ConfigureAwait(false);
            _persisted.TrySetResult(firedAt);
        }
    }

    [Fact]
    public void SCH_U_027_Wall_clock_jump_backwards_recomputes_without_double_firing()
    {
        using var logs = new LogCollector();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var logger = loggerFactory.CreateLogger<TimeTriggerService>();

        var start = new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero);
        var time = TestTime.CreateProvider(start);
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, logger);

        int fires = 0;
        using var reg = triggerService.Register("test", "one-shot-jump", new TriggerSpec.At(start.AddMinutes(5)), MissedPolicy.Skip, (_, _) =>
        {
            fires++;
            return ValueTask.CompletedTask;
        });

        // Fire the trigger
        triggerService.ProcessDue(start.AddMinutes(6));
        fires.ShouldBe(1);

        // Jump wall clock backward by 10 minutes (delta > 1 minute)
        triggerService.ProcessDue(start.AddMinutes(-4));

        logs.WithEventId(2352).ShouldNotBeEmpty();

        // Advance past original fire time again: At trigger must NOT fire twice
        triggerService.ProcessDue(start.AddMinutes(15));
        fires.ShouldBe(1);
    }

    [Fact]
    public void SCH_U_028_Callbacks_run_on_side_effect_queue_unless_dispatcher_provided()
    {
        var time = TestTime.CreateProvider();
        var calService = new CalendarService(NullLogger<CalendarService>.Instance);
        var stateStore = new InMemoryTriggerStateStore();
        var geoProvider = new DefaultLocationGeoProvider();
        using var triggerService = new TimeTriggerService(time, calService, stateStore, geoProvider, NullLogger<TimeTriggerService>.Instance);

        var customDispatcher = new TrackingDispatcher();
        bool customFired = false;
        bool defaultFired = false;

        using var regDefault = triggerService.Register("test", "default-disp", new TriggerSpec.At(time.GetUtcNow().AddSeconds(5)), MissedPolicy.Skip, (_, _) =>
        {
            defaultFired = true;
            return ValueTask.CompletedTask;
        });

        using var regCustom = triggerService.RegisterWithDispatcher("test", "custom-disp", new TriggerSpec.At(time.GetUtcNow().AddSeconds(5)), MissedPolicy.Skip, customDispatcher, (_, _) =>
        {
            customFired = true;
            return ValueTask.CompletedTask;
        });

        triggerService.ProcessDue(time.GetUtcNow().AddSeconds(6));

        customDispatcher.DispatchedCount.ShouldBe(1);
        customFired.ShouldBe(true);
        defaultFired.ShouldBe(true);
    }

    private sealed class TrackingDispatcher : ITriggerDispatcher
    {
        public int DispatchedCount { get; private set; }

        public async ValueTask DispatchAsync(Func<CancellationToken, ValueTask> work, CancellationToken ct)
        {
            DispatchedCount++;
            await work(ct).ConfigureAwait(false);
        }
    }
}
