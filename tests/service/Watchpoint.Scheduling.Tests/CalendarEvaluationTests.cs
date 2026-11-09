using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Watchpoint.Testing.Logging;
using Xunit;

namespace Watchpoint.Scheduling.Tests;

[Trait("layer", "U")]
[Trait("spec", "SCH")]
public sealed class CalendarEvaluationTests
{
    [Fact]
    public void SCH_U_001_Weekly_ranges_active_inactive_at_boundaries()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Monday] =
            [
                new TimeRange(new LocalTime(9, 0), new LocalTime(17, 0)),
                TimeRange.UntilMidnight(new LocalTime(20, 0)),
            ],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "office-hours",
            LocalizedName.Of("Office"),
            "UTC",
            weekly,
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        service.IsActive("office-hours", new DateTimeOffset(2026, 1, 5, 8, 59, 59, TimeSpan.Zero)).ShouldBeFalse();
        service.IsActive("office-hours", new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("office-hours", new DateTimeOffset(2026, 1, 5, 16, 59, 59, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("office-hours", new DateTimeOffset(2026, 1, 5, 17, 0, 0, TimeSpan.Zero)).ShouldBeFalse();

        service.IsActive("office-hours", new DateTimeOffset(2026, 1, 5, 20, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("office-hours", new DateTimeOffset(2026, 1, 5, 23, 59, 59, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("office-hours", new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
    }

    [Fact]
    public void SCH_U_002_Overlapping_ranges_rejected()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Monday] =
            [
                new TimeRange(new LocalTime(9, 0), new LocalTime(12, 0)),
                new TimeRange(new LocalTime(11, 30), new LocalTime(17, 0)),
            ],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "invalid-overlap",
            LocalizedName.Of("Overlap"),
            "UTC",
            weekly,
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        var result = service.Upsert(cal);

        result.IsSuccess.ShouldBeFalse();
        result.Error.Code.Value.ShouldBe(SchedulingErrors.RangeOverlapCode);
    }

    [Fact]
    public void SCH_U_003_Overnight_via_two_ranges()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Monday] = [TimeRange.UntilMidnight(new LocalTime(22, 0))],
            [IsoDayOfWeek.Tuesday] = [new TimeRange(new LocalTime(0, 0), new LocalTime(6, 0))],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "night-shift",
            LocalizedName.Of("Night"),
            "UTC",
            weekly,
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        service.IsActive("night-shift", new DateTimeOffset(2026, 1, 5, 21, 59, 0, TimeSpan.Zero)).ShouldBeFalse();
        service.IsActive("night-shift", new DateTimeOffset(2026, 1, 5, 22, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("night-shift", new DateTimeOffset(2026, 1, 5, 23, 59, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("night-shift", new DateTimeOffset(2026, 1, 6, 0, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("night-shift", new DateTimeOffset(2026, 1, 6, 5, 59, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("night-shift", new DateTimeOffset(2026, 1, 6, 6, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
    }

    [Fact]
    public void SCH_U_004_Exception_Closed_Open_Custom_precedence_over_weekly()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Monday] = [new TimeRange(new LocalTime(9, 0), new LocalTime(17, 0))],
        };

        var exceptions = new List<CalendarException>
        {
            new(new DateOnly(2026, 1, 5), ExceptionMode.Closed, null, "Office closed"),
            new(new DateOnly(2026, 1, 6), ExceptionMode.Open, null, "All day open"),
            new(new DateOnly(2026, 1, 7), ExceptionMode.Custom, [new TimeRange(new LocalTime(14, 0), new LocalTime(16, 0))], "Afternoon"),
        };

        var cal = new Calendar(
            CalendarId.New(),
            "exceptions-test",
            LocalizedName.Of("Test"),
            "UTC",
            weekly,
            exceptions,
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        service.IsActive("exceptions-test", new DateTimeOffset(2026, 1, 5, 10, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
        service.IsActive("exceptions-test", new DateTimeOffset(2026, 1, 6, 2, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("exceptions-test", new DateTimeOffset(2026, 1, 7, 13, 59, 0, TimeSpan.Zero)).ShouldBeFalse();
        service.IsActive("exceptions-test", new DateTimeOffset(2026, 1, 7, 14, 30, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("exceptions-test", new DateTimeOffset(2026, 1, 7, 16, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
    }

    [Fact]
    public void SCH_U_005_Holiday_set_precedence_order_and_recurring_yearly_holidays()
    {
        var set1Id = HolidaySetId.New();
        var set2Id = HolidaySetId.New();

        var set1 = new HolidaySet(
            set1Id,
            LocalizedName.Of("National"),
            "FR",
            [new Holiday(new DateOnly(2020, 5, 1), LocalizedName.Of("Labour Day"), true)],
            1);

        var set2 = new HolidaySet(
            set2Id,
            LocalizedName.Of("Site Special"),
            null,
            [new Holiday(new DateOnly(2026, 5, 1), LocalizedName.Of("Special Opening"), false)],
            1);

        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Friday] = [new TimeRange(new LocalTime(8, 0), new LocalTime(18, 0))],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "holidays-order",
            LocalizedName.Of("Holidays"),
            "UTC",
            weekly,
            [],
            [
                new HolidaySetUse(set1Id, ExceptionMode.Closed, null),
                new HolidaySetUse(set2Id, ExceptionMode.Custom, [new TimeRange(new LocalTime(10, 0), new LocalTime(12, 0))]),
            ],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.UpsertHolidaySet(set1);
        service.UpsertHolidaySet(set2);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        service.IsActive("holidays-order", new DateTimeOffset(2026, 5, 1, 11, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
        service.IsActive("holidays-order", new DateTimeOffset(2027, 5, 1, 11, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
    }

    [Fact]
    public void SCH_U_006_Validity_window()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Monday] = [new TimeRange(new LocalTime(9, 0), new LocalTime(17, 0))],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "validity-cal",
            LocalizedName.Of("Validity"),
            "UTC",
            weekly,
            [],
            [],
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 28),
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        service.IsActive("validity-cal", new DateTimeOffset(2026, 1, 26, 10, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
        service.IsActive("validity-cal", new DateTimeOffset(2026, 2, 9, 10, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("validity-cal", new DateTimeOffset(2026, 3, 2, 10, 0, 0, TimeSpan.Zero)).ShouldBeFalse();
    }

    [Fact]
    public void SCH_U_007_Calendar_time_zone_vs_site_time_zone()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Monday] = [new TimeRange(new LocalTime(9, 0), new LocalTime(17, 0))],
        };

        var calA = new Calendar(
            CalendarId.New(),
            "cal-dubai",
            LocalizedName.Of("Dubai"),
            "Asia/Dubai",
            weekly,
            [],
            [],
            null,
            null,
            1);

        var calB = new Calendar(
            CalendarId.New(),
            "cal-site",
            LocalizedName.Of("Site"),
            null,
            weekly,
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance)
        {
            SiteTimeZone = "Europe/Paris",
        };
        service.Upsert(calA).IsSuccess.ShouldBeTrue();
        service.Upsert(calB).IsSuccess.ShouldBeTrue();

        var instant = new DateTimeOffset(2026, 1, 5, 6, 0, 0, TimeSpan.Zero);
        service.IsActive("cal-dubai", instant).ShouldBeTrue();
        service.IsActive("cal-site", instant).ShouldBeFalse();
    }

    [Fact]
    public void SCH_U_008_DST_spring_gap_range_starting_in_the_gap_starts_after_it()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Sunday] = [new TimeRange(new LocalTime(2, 30), new LocalTime(5, 0))],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "dst-spring",
            LocalizedName.Of("DST Spring"),
            "Europe/Paris",
            weekly,
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        service.IsActive("dst-spring", new DateTimeOffset(2026, 3, 29, 0, 59, 59, TimeSpan.Zero)).ShouldBeFalse();
        service.IsActive("dst-spring", new DateTimeOffset(2026, 3, 29, 1, 0, 0, TimeSpan.Zero)).ShouldBeTrue();
    }

    [Fact]
    public void SCH_U_009_DST_autumn_overlap_range_ending_in_repeated_hour_ends_at_later_occurrence()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Sunday] = [new TimeRange(new LocalTime(1, 0), new LocalTime(2, 30))],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "dst-autumn",
            LocalizedName.Of("DST Autumn"),
            "Europe/Paris",
            weekly,
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        service.IsActive("dst-autumn", new DateTimeOffset(2026, 10, 25, 1, 15, 0, TimeSpan.Zero)).ShouldBeTrue();
        service.IsActive("dst-autumn", new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero)).ShouldBeFalse();
    }

    [Fact]
    public void SCH_U_010_NextTransition_across_days_holidays_and_DST()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Monday] = [new TimeRange(new LocalTime(9, 0), new LocalTime(17, 0))],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "transitions-cal",
            LocalizedName.Of("Transitions"),
            "UTC",
            weekly,
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        var next1 = service.NextTransition("transitions-cal", new DateTimeOffset(2026, 1, 5, 8, 0, 0, TimeSpan.Zero));
        next1.ShouldBe(new DateTimeOffset(2026, 1, 5, 9, 0, 0, TimeSpan.Zero));

        var next2 = service.NextTransition("transitions-cal", new DateTimeOffset(2026, 1, 5, 12, 0, 0, TimeSpan.Zero));
        next2.ShouldBe(new DateTimeOffset(2026, 1, 5, 17, 0, 0, TimeSpan.Zero));

        var next3 = service.NextTransition("transitions-cal", new DateTimeOffset(2026, 1, 5, 18, 0, 0, TimeSpan.Zero));
        next3.ShouldBe(new DateTimeOffset(2026, 1, 12, 9, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void SCH_U_011_GetPeriods_preview_limited_to_62_days()
    {
        var weekly = new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>
        {
            [IsoDayOfWeek.Monday] = [new TimeRange(new LocalTime(9, 0), new LocalTime(17, 0))],
        };

        var cal = new Calendar(
            CalendarId.New(),
            "preview-cal",
            LocalizedName.Of("Preview"),
            "UTC",
            weekly,
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        var start = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var end = start.AddDays(120);

        var periods = service.GetPeriods("preview-cal", start, end);
        periods.ShouldNotBeEmpty();

        var maxLimit = start.AddDays(62);
        periods.All(p => p.End <= maxLimit).ShouldBeTrue();
    }

    [Fact]
    public void SCH_U_012_Unknown_key_IsActive_false_logged_once()
    {
        using var logs = new LogCollector();
        var loggerFactory = LoggerFactory.Create(b => b.AddProvider(logs));
        var logger = loggerFactory.CreateLogger<CalendarService>();

        var service = new CalendarService(logger);

        var now = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        service.IsActive("unknown-key-xyz", now).ShouldBeFalse();
        service.IsActive("unknown-key-xyz", now.AddMinutes(5)).ShouldBeFalse();

        logs.WithEventId(2354).Count().ShouldBe(1);
    }

    [Theory]
    [InlineData("valid-key", true)]
    [InlineData("a1", true)]
    [InlineData("k", false)]
    [InlineData("UpperKey", false)]
    [InlineData("1startwithdigit", false)]
    public void SCH_U_015_Key_pattern_and_uniqueness(string key, bool valid)
    {
        var cal = new Calendar(
            CalendarId.New(),
            key,
            LocalizedName.Of("Key Test"),
            "UTC",
            new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>(),
            [],
            [],
            null,
            null,
            1);

        var service = new CalendarService(NullLogger<CalendarService>.Instance);
        var result = service.Upsert(cal);

        if (valid)
        {
            result.IsSuccess.ShouldBeTrue();

            var calDuplicate = new Calendar(
                CalendarId.New(),
                key,
                LocalizedName.Of("Duplicate"),
                "UTC",
                new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>(),
                [],
                [],
                null,
                null,
                1);

            var dupResult = service.Upsert(calDuplicate);
            dupResult.IsSuccess.ShouldBeFalse();
            dupResult.Error.Code.Value.ShouldBe(SchedulingErrors.CalendarKeyTakenCode);
        }
        else
        {
            result.IsSuccess.ShouldBeFalse();
        }
    }
}
