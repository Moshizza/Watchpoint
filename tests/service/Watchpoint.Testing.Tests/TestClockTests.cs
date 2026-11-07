using Shouldly;
using Watchpoint.Testing.Time;
using Xunit;

namespace Watchpoint.Testing.Tests;

[Trait("layer", "U")]
[Trait("spec", "TST")]
public sealed class TestClockTests
{
    [Fact]
    public void TST_U_001_Advance_fires_timers_in_order()
    {
        var clock = new TestClock();
        var fired = new List<(string Name, DateTimeOffset At)>();
        using var second = clock.CreateTimer(_ => fired.Add(("2s", clock.GetUtcNow())), null, TimeSpan.FromSeconds(2), Timeout.InfiniteTimeSpan);
        using var first = clock.CreateTimer(_ => fired.Add(("1s", clock.GetUtcNow())), null, TimeSpan.FromSeconds(1), Timeout.InfiniteTimeSpan);

        clock.Advance(TimeSpan.FromSeconds(3));

        fired.Select(f => f.Name).ShouldBe(["1s", "2s"]);
        clock.GetUtcNow().ShouldBe(TestClock.DefaultStart.AddSeconds(3));
    }

    [Fact]
    public void TST_U_001_AdvanceTo_moves_forward_only()
    {
        var clock = new TestClock();
        var target = TestClock.DefaultStart.AddMinutes(5);

        clock.AdvanceTo(target);

        clock.GetUtcNow().ShouldBe(target);
        Should.Throw<ArgumentOutOfRangeException>(() => clock.AdvanceTo(target.AddSeconds(-1)));
    }
}
