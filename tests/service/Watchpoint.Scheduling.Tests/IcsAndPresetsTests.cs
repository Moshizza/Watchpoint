using Microsoft.Extensions.Logging.Abstractions;
using NodaTime;
using Shouldly;
using Watchpoint.Kernel.Ids;
using Watchpoint.Kernel.Text;
using Xunit;

namespace Watchpoint.Scheduling.Tests;

[Trait("layer", "U")]
[Trait("spec", "SCH")]
public sealed class IcsAndPresetsTests
{
    [Fact]
    public void SCH_U_013_ICS_import_all_day_events_yearly_RRULE_warnings_for_unsupported_content()
    {
        var ics = """
            BEGIN:VCALENDAR
            VERSION:2.0
            PRODID:-//Test//EN
            X-CUSTOM-PROP:IGNORED
            BEGIN:VEVENT
            UID:evt-1
            DTSTART;VALUE=DATE:20260101
            SUMMARY:New Year's Day
            RRULE:FREQ=YEARLY
            END:VEVENT
            BEGIN:VEVENT
            UID:evt-2
            DTSTART:20260102T090000Z
            SUMMARY:Timed Event
            END:VEVENT
            BEGIN:VEVENT
            UID:evt-3
            DTSTART;VALUE=DATE:20260501
            SUMMARY:Labour Day
            RRULE:FREQ=MONTHLY;BYDAY=1MO
            END:VEVENT
            END:VCALENDAR
            """;

        var result = IcsHolidayParser.Parse(ics);
        result.IsSuccess.ShouldBeTrue();

        var parsed = result.Value;
        parsed.Holidays.Count.ShouldBe(2);

        var h1 = parsed.Holidays[0];
        h1.Date.ShouldBe(new DateOnly(2026, 1, 1));
        h1.Name.Default.ShouldBe("New Year's Day");
        h1.RecursYearly.ShouldBeTrue();

        var h2 = parsed.Holidays[1];
        h2.Date.ShouldBe(new DateOnly(2026, 5, 1));
        h2.Name.Default.ShouldBe("Labour Day");
        h2.RecursYearly.ShouldBeFalse();

        parsed.Warnings.ShouldNotBeEmpty();
        parsed.Warnings.Any(w => w.Contains("not an all-day event", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
        parsed.Warnings.Any(w => w.Contains("Unsupported RRULE FREQ", StringComparison.OrdinalIgnoreCase)).ShouldBeTrue();
    }

    [Fact]
    public void SCH_U_014_ICS_export_round_trip()
    {
        var set = new HolidaySet(
            HolidaySetId.New(),
            LocalizedName.Of("Roundtrip Set"),
            "FR",
            [
                new Holiday(new DateOnly(2026, 1, 1), LocalizedName.Of("New Year's Day"), true),
                new Holiday(new DateOnly(2026, 7, 14), LocalizedName.Of("Bastille Day, National Holiday; Special"), false),
            ],
            1);

        var exported = IcsHolidayParser.Export(set);
        exported.ShouldContain("BEGIN:VCALENDAR");
        exported.ShouldContain("SUMMARY:Bastille Day\\, National Holiday\\; Special");

        var reimport = IcsHolidayParser.Parse(exported);
        reimport.IsSuccess.ShouldBeTrue();

        var holidays = reimport.Value.Holidays;
        holidays.Count.ShouldBe(2);

        holidays[0].Date.ShouldBe(new DateOnly(2026, 1, 1));
        holidays[0].Name.Default.ShouldBe("New Year's Day");
        holidays[0].RecursYearly.ShouldBeTrue();

        holidays[1].Date.ShouldBe(new DateOnly(2026, 7, 14));
        holidays[1].Name.Default.ShouldBe("Bastille Day, National Holiday; Special");
        holidays[1].RecursYearly.ShouldBeFalse();
    }

    [Fact]
    public async Task SCH_U_029_Calendar_in_use_cannot_be_deleted()
    {
        var calId = CalendarId.New();
        var cal = new Calendar(
            calId,
            "in-use-cal",
            LocalizedName.Of("In Use"),
            "UTC",
            new Dictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>>(),
            [],
            [],
            null,
            null,
            1);

        var fakeRefProvider = new FakeCalendarReferenceProvider();
        fakeRefProvider.SetReferences(calId, ["Alarm Rule AR-001 (Priority Schedule)", "Automation Step AUTO-10"]);

        var service = new CalendarService(NullLogger<CalendarService>.Instance, [fakeRefProvider]);
        service.Upsert(cal).IsSuccess.ShouldBeTrue();

        var ct = TestContext.Current.CancellationToken;

        var delResult = await service.DeleteAsync(calId, ct);
        delResult.IsSuccess.ShouldBeFalse();
        delResult.Error.Code.Value.ShouldBe(SchedulingErrors.CalendarInUseCode);
        delResult.Error.Args["references"]!.ToString()!.ShouldContain("Alarm Rule AR-001");

        fakeRefProvider.SetReferences(calId, []);
        var delSuccess = await service.DeleteAsync(calId, ct);
        delSuccess.IsSuccess.ShouldBeTrue();
    }

    [Fact]
    public void SCH_U_030_Presets_import_produce_valid_holiday_sets()
    {
        foreach (var country in HolidayPresets.SupportedCountries)
        {
            var preset = HolidayPresets.GetPreset(country, 2026);
            preset.CountryCode.ShouldBe(country);
            preset.Holidays.ShouldNotBeEmpty();

            preset.Holidays.All(h => h.Date.Year is 2026 or 2027).ShouldBeTrue();

            var ics = IcsHolidayParser.Export(preset);
            var parseRes = IcsHolidayParser.Parse(ics);
            parseRes.IsSuccess.ShouldBeTrue();
            parseRes.Value.Holidays.Count.ShouldBe(preset.Holidays.Count);
        }
    }

    private sealed class FakeCalendarReferenceProvider : ICalendarReferenceProvider
    {
        private readonly Dictionary<CalendarId, IReadOnlyList<string>> _refs = [];

        public void SetReferences(CalendarId id, IReadOnlyList<string> references) => _refs[id] = references;

        public ValueTask<IReadOnlyList<string>> GetReferencesAsync(CalendarId calendarId, CancellationToken ct)
        {
            if (_refs.TryGetValue(calendarId, out var list))
            {
                return ValueTask.FromResult(list);
            }

            return ValueTask.FromResult<IReadOnlyList<string>>([]);
        }
    }
}
