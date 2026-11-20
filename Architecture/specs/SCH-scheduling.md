# SCH — Scheduling and Calendars

| | |
|---|---|
| Spec id | SCH |
| Projects | `src/service/Watchpoint.Scheduling` |
| Milestone | M2 |
| Tasks | T2.08 |
| Design docs | [Core runtime §4](../../docs/modules/core-runtime.md#4-scheduler-and-calendars) |

## 1. Purpose and scope

- **Calendars**: named weekly schedules with exceptions and holiday sets, in a time zone.
  Used by rights (when a grant applies), alarm rules (active hours, priority by time), automations,
  notifications (on-call), expressions (`inSchedule`).
- **Holiday sets**: per country/site, editable, importable from ICS.
- **Time triggers** for other modules: one-shot, cron, calendar edges, intervals, sunrise/sunset —
  with defined behaviour across restarts and DST.

## 2. Dependencies

K (incl. NodaTime), STO, REG (site and location time zones and geo), Cronos (MIT) for cron
expressions. Used by ALM, RGT, AUT, NOT, EXP, RPT.

## 3. Model

Log event ids: 2350–2399 ([README §8](../README.md#8-observability-conventions)).

```csharp
public sealed record Calendar(CalendarId Id, string Key /* ^[a-z][a-z0-9-]{1,63}$, unique */, LocalizedName Name,
    string? TimeZone /* IANA; null = site time zone */, IReadOnlyDictionary<IsoDayOfWeek, IReadOnlyList<TimeRange>> Weekly,
    IReadOnlyList<CalendarException> Exceptions, IReadOnlyList<HolidaySetUse> HolidaySets,
    DateOnly? ValidFrom, DateOnly? ValidUntil, long Version);

public sealed record TimeRange(LocalTime Start, LocalTime End);   // End exclusive; "24:00" allowed as End; Start < End
public sealed record CalendarException(DateOnly Date, ExceptionMode Mode, IReadOnlyList<TimeRange>? Ranges, string? Note);
public enum ExceptionMode { Closed /* inactive all day */, Open /* active all day */, Custom /* use Ranges */ }
public sealed record HolidaySetUse(HolidaySetId Set, ExceptionMode Mode /* usually Closed */, IReadOnlyList<TimeRange>? Ranges);

public sealed record HolidaySet(HolidaySetId Id, LocalizedName Name, string? CountryCode /* ISO 3166-1 alpha-2 */,
    IReadOnlyList<Holiday> Holidays, long Version);
public sealed record Holiday(DateOnly Date, LocalizedName Name, bool RecursYearly);
```

(`HolidaySetId` is added to the Kernel id list, prefix `hol`.)

Rules: ranges of a day must not overlap (validated, `scheduling.range.overlap`); overnight
periods are expressed as two ranges (22:00–24:00 on Monday, 00:00–06:00 on Tuesday). Precedence
for a given date: explicit `Exceptions` > holiday sets (in list order) > weekly.

## 4. Evaluation contract

```csharp
public interface ICalendarService
{
    bool IsActive(string calendarKey, DateTimeOffset instant);                // false for unknown keys (logged once)
    Result<bool> IsActiveStrict(CalendarId id, DateTimeOffset instant);
    DateTimeOffset? NextTransition(string calendarKey, DateTimeOffset after, int maxDaysAhead = 400);
    IReadOnlyList<ActivePeriod> GetPeriods(string calendarKey, DateTimeOffset from, DateTimeOffset to);  // UI previews (≤ 62 days)
    Result<Calendar> Get(string key);
    event EventHandler<CalendarChangedEventArgs>? Changed;
}
public sealed record ActivePeriod(DateTimeOffset Start, DateTimeOffset End);
```

DST behaviour (local times converted with NodaTime, `Resolvers.LenientResolver`): a range starting
in a skipped hour starts at the first valid instant after the gap; a range ending in a repeated
hour ends at the **later** occurrence. All evaluations are pure functions of the calendar
document and the instant (cached per calendar version for the current day).

## 5. Time triggers

```csharp
public abstract record TriggerSpec
{
    public sealed record At(DateTimeOffset Utc) : TriggerSpec;                                         // one-shot
    public sealed record Cron(string Expression /* 5 fields */, string? TimeZone) : TriggerSpec;       // Cronos
    public sealed record CalendarEdge(string CalendarKey, EdgeKind Edge /* Start | End | Both */) : TriggerSpec;
    public sealed record Every(TimeSpan Interval /* ≥ PT1S */, DateTimeOffset? Anchor) : TriggerSpec;
    public sealed record Sun(SunEvent Event /* Sunrise | Sunset */, TimeSpan Offset, LocationId? Location /* geo from REG; null = site */) : TriggerSpec;
}
public enum MissedPolicy { Skip, FireOnce }

public interface ITimeTriggerService
{
    // Registration is in-memory; owners re-register at start-up. Key must be stable across restarts.
    IDisposable Register(string owner, string key, TriggerSpec spec, MissedPolicy missed,
                         Func<TriggerFired, CancellationToken, ValueTask> callback);
    DateTimeOffset? NextFire(string owner, string key);
}
public sealed record TriggerFired(string Owner, string Key, DateTimeOffset ScheduledAt, DateTimeOffset FiredAt, bool CatchUp);
```

- A single timer loop (driven by `TimeProvider`) keeps a min-heap of next fire times; callbacks
  run on the side-effect queue class `Background` (PIP §9) unless the owner passes its own
  dispatcher (ALM/AUT post to partitions).
- `wp_scheduling.trigger_state(owner, key, last_fired_at)` persists the last fire time (updated
  after each callback completes). At start-up, for `FireOnce` registrations whose schedule had at
  least one occurrence between `last_fired_at` and now, the callback fires **once** with
  `CatchUp = true`; `Skip` ignores missed occurrences.
- Sunrise/sunset: NOAA solar position algorithm (own implementation, ±1 min accuracy) using the
  location's geo point (REG) or the site's; polar day/night → no event that day (logged once).
- Clock jumps: if the wall clock moves backwards by more than 1 minute, the heap is recomputed;
  one-shot `At` triggers never fire twice (state check).

## 6. Data (`wp_scheduling`)

| Table | Columns |
|---|---|
| `calendars` | id, key (unique), name jsonb, time_zone, weekly jsonb, exceptions jsonb, holiday_sets jsonb, valid_from, valid_until, row_version, timestamps |
| `holiday_sets` | id, name jsonb, country_code, holidays jsonb, row_version, timestamps |
| `trigger_state` | owner text, key text, last_fired_at timestamptz — PK (owner, key) |

## 7. API

| Method and path | Permission | Description |
|---|---|---|
| `GET /api/v1/calendars`, `GET /api/v1/calendars/{id}` | authenticated | |
| `GET /api/v1/calendars/{id}/periods?from=&to=` | authenticated | Preview (≤ 62 days) |
| `POST/PATCH/DELETE /api/v1/calendars…` | `config.calendars.edit` (M3 via drafts) | Delete refused if referenced (`scheduling.calendar.in_use` with referencing objects listed by modules through `ICalendarReferenceProvider`) |
| `GET/POST/PATCH/DELETE /api/v1/holiday-sets…` | `config.calendars.edit` | |
| `POST /api/v1/holiday-sets/import` | `config.calendars.edit` | ICS upload (≤ 1 MB): `VEVENT` with all-day `DTSTART;VALUE=DATE`, optional `RRULE:FREQ=YEARLY`; other content ignored with warnings |
| `GET /api/v1/holiday-sets/{id}/export` | authenticated | ICS |

Shipped presets (data files, imported on demand from the UI): public holidays for FR, MA, DZ, TN,
AE, SA, QA, US, GB, DE, ES, TR for the current and next year (Islamic holidays marked
"date to confirm" because they depend on moon sighting; administrators adjust).

## 8. Errors, observability

Errors: `scheduling.calendar.not_found`, `scheduling.calendar.key_taken`,
`scheduling.range.overlap`, `scheduling.range.invalid` (start ≥ end), `scheduling.calendar.in_use`,
`scheduling.ics.invalid`, `scheduling.cron.invalid`, `scheduling.timezone.invalid`.

Logs 2350–2399: trigger registered (debug), catch-up fired (info), clock jump detected (warning),
polar day/night (info). Metrics: `watchpoint.scheduling.triggers` (gauge), `…fired` (counter by
owner), `…lateness` (histogram ms between scheduled and fired).

## 9. Tests

| Id | Test |
|---|---|
| SCH-U-001 | Weekly ranges active/inactive at boundaries (start inclusive, end exclusive, 24:00) |
| SCH-U-002 | Overlapping ranges rejected |
| SCH-U-003 | Overnight via two ranges |
| SCH-U-004 | Exception Closed / Open / Custom precedence over weekly |
| SCH-U-005 | Holiday set precedence order and recurring yearly holidays |
| SCH-U-006 | Validity window |
| SCH-U-007 | Calendar time zone vs site time zone |
| SCH-U-008 | DST spring gap: range starting in the gap starts after it |
| SCH-U-009 | DST autumn overlap: range ending in the repeated hour ends at the later occurrence |
| SCH-U-010 | NextTransition across days, holidays and DST |
| SCH-U-011 | GetPeriods preview limited to 62 days |
| SCH-U-012 | Unknown key → IsActive false, logged once |
| SCH-U-013 | ICS import: all-day events, yearly RRULE, warnings for unsupported content |
| SCH-U-014 | ICS export round-trip |
| SCH-U-015 | Key pattern and uniqueness |
| SCH-U-016 | At trigger fires once |
| SCH-U-017 | Cron trigger with time zone, incl. DST days (Cronos semantics) |
| SCH-U-018 | Invalid cron rejected |
| SCH-U-019 | Calendar edge triggers at starts/ends |
| SCH-U-020 | Calendar change recomputes edge triggers |
| SCH-U-021 | Every interval with anchor |
| SCH-U-022 | Sunrise/sunset for Paris, Casablanca, Dubai within ±2 min of reference values |
| SCH-U-023 | Polar day → no event, logged |
| SCH-U-024 | Missed FireOnce fires once with CatchUp after restart |
| SCH-U-025 | Missed Skip does not fire |
| SCH-U-026 | Last fired persisted only after callback completion |
| SCH-U-027 | Wall clock jump backwards recomputes without double firing |
| SCH-U-028 | Callbacks run on side-effect queue unless dispatcher provided |
| SCH-U-029 | Calendar in use cannot be deleted (reference provider) |
| SCH-U-030 | Presets import produce valid holiday sets |

## 10. Tasks

| Task | Sections | Tests |
|---|---|---|
| T2.08 | All | SCH-U-001…SCH-U-030 |
