using NodaTime;
using Shouldly;
using Watchpoint.Kernel.Results;
using Watchpoint.Kernel.Time;
using Xunit;

namespace Watchpoint.Kernel.Tests.Time;

public sealed class SiteTimeZoneTests
{
    private static readonly SiteTimeZone _paris = SiteTimeZone.FromIana("Europe/Paris").Value;

    [Fact]
    public void K_U_034_SiteTimeZone_FromIana()
    {
        _paris.Id.ShouldBe("Europe/Paris");

        foreach (var id in new[] { "Mars/Base", "", "europe/paris" })
        {
            var result = SiteTimeZone.FromIana(id);
            result.IsSuccess.ShouldBeFalse();
            result.Error.Code.ShouldBe(CommonErrorCodes.ValueInvalidFormat);
        }

        _paris.ToLocal(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero)).ShouldBe(new LocalDateTime(2026, 7, 1, 12, 0));
        _paris.ToUtc(new LocalDateTime(2026, 7, 1, 12, 0), AmbiguityPolicy.Reject).Value
            .ShouldBe(new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero));
    }

    [Fact]
    public void K_U_035_DST_gap_handling()
    {
        var local = new LocalDateTime(2026, 3, 29, 2, 30);

        var rejected = _paris.ToUtc(local, AmbiguityPolicy.Reject);
        rejected.IsSuccess.ShouldBeFalse();
        rejected.Error.Code.ShouldBe(CommonErrorCodes.TimeInvalidLocal);
        rejected.Error.Args["reason"].ShouldBe("gap");

        var later = _paris.ToUtc(local, AmbiguityPolicy.LaterOffset).Value;
        later.ShouldBe(new DateTimeOffset(2026, 3, 29, 1, 30, 0, TimeSpan.Zero));
        later.Offset.ShouldBe(TimeSpan.Zero);

        _paris.ToUtc(local, AmbiguityPolicy.EarlierOffset).Value.ShouldBe(new DateTimeOffset(2026, 3, 29, 0, 30, 0, TimeSpan.Zero));
    }

    [Fact]
    public void K_U_036_DST_overlap_handling()
    {
        var local = new LocalDateTime(2026, 10, 25, 2, 30);

        _paris.ToUtc(local, AmbiguityPolicy.EarlierOffset).Value.ShouldBe(new DateTimeOffset(2026, 10, 25, 0, 30, 0, TimeSpan.Zero));
        _paris.ToUtc(local, AmbiguityPolicy.LaterOffset).Value.ShouldBe(new DateTimeOffset(2026, 10, 25, 1, 30, 0, TimeSpan.Zero));

        var rejected = _paris.ToUtc(local, AmbiguityPolicy.Reject);
        rejected.Error.Code.ShouldBe(CommonErrorCodes.TimeInvalidLocal);
        rejected.Error.Args["reason"].ShouldBe("overlap");
    }
}
