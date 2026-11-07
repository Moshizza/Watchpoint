using System.Globalization;
using NodaTime;
using NodaTime.Text;
using Watchpoint.Kernel.Results;

namespace Watchpoint.Kernel.Time;

/// <summary>An IANA time zone of a site, with conversions between UTC instants and local times (NodaTime, TZDB).</summary>
public sealed class SiteTimeZone
{
    private readonly DateTimeZone _zone;

    private SiteTimeZone(DateTimeZone zone) => _zone = zone;

    /// <summary>Gets the IANA id, e.g. <c>Europe/Paris</c>.</summary>
    public string Id => _zone.Id;

    /// <summary>Looks up a time zone by IANA id.</summary>
    /// <param name="tzId">The IANA id, e.g. <c>Europe/Paris</c> (case-sensitive).</param>
    /// <returns>The time zone, or <c>common.value.invalid_format</c> for an unknown id.</returns>
    public static Result<SiteTimeZone> FromIana(string tzId)
    {
        var zone = string.IsNullOrEmpty(tzId) ? null : DateTimeZoneProviders.Tzdb.GetZoneOrNull(tzId);
        return zone is null ? CommonErrors.InvalidFormat("IANA time zone id") : new SiteTimeZone(zone);
    }

    /// <summary>Converts an instant to the local date and time of the zone.</summary>
    /// <param name="utc">The instant (any offset).</param>
    /// <returns>The local date and time.</returns>
    public LocalDateTime ToLocal(DateTimeOffset utc) => Instant.FromDateTimeOffset(utc).InZone(_zone).LocalDateTime;

    /// <summary>Converts a local date and time of the zone to a UTC instant.</summary>
    /// <param name="local">The local date and time.</param>
    /// <param name="policy">How to resolve daylight-saving gaps and overlaps.</param>
    /// <returns>The instant with offset zero, or <c>common.time.invalid_local</c> when rejected by the policy.</returns>
    public Result<DateTimeOffset> ToUtc(LocalDateTime local, AmbiguityPolicy policy)
    {
        var mapping = _zone.MapLocal(local);
        ZonedDateTime zoned;
        switch (mapping.Count)
        {
            case 1:
                zoned = mapping.Single();
                break;
            case 2:
                if (policy == AmbiguityPolicy.Reject)
                {
                    return CommonErrors.TimeInvalidLocal(Format(local), "overlap");
                }

                zoned = policy == AmbiguityPolicy.EarlierOffset ? mapping.First() : mapping.Last();
                break;
            default:
                if (policy == AmbiguityPolicy.Reject)
                {
                    return CommonErrors.TimeInvalidLocal(Format(local), "gap");
                }

                // Gap: the interval just before has the earlier offset, the one just after the later offset.
                var offset = policy == AmbiguityPolicy.EarlierOffset
                    ? mapping.LateInterval.WallOffset
                    : mapping.EarlyInterval.WallOffset;
                zoned = new OffsetDateTime(local, offset).InZone(_zone);
                break;
        }

        return zoned.ToInstant().ToDateTimeOffset();
    }

    /// <inheritdoc />
    public override string ToString() => Id;

    private static string Format(LocalDateTime local) =>
        LocalDateTimePattern.GeneralIso.Format(local).ToString(CultureInfo.InvariantCulture);
}
